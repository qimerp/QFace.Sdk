using System.Diagnostics.Metrics;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Outbox.Transports;
using QimErp.Shared.Common.Services.MultiTenancy;
using Xunit;

namespace QimErp.Shared.Common.Tests.Outbox;

public class OutboxUnknownEventTypeTests
{
    private const string FutureEventType = "OutboxFutureEventNotKnownToOlderRelays";
    private const string RegisteredEventType = "OutboxRegisteredRelayEvent";

    public sealed record RelayNotification(string Name) : INotification;

    public sealed record ScannedOnlyRelayNotification(string Name) : INotification;

    [Fact]
    public async Task Unknown_in_process_event_type_is_not_claimed_and_keeps_its_attempts()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, services => services.AddOutboxEventType<RelayNotification>(RegisteredEventType));
        var unknownId = await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess, attempts: 2);
        var knownId = await SeedAsync(provider, RegisteredEventType, OutboxTransports.InProcess);

        var claimed = await ClaimAsync(provider, "old-relay");

        claimed.Select(m => m.Id).Should().Equal(knownId);
        var unknown = await ReadAsync(provider, unknownId);
        unknown.Attempts.Should().Be(2);
        unknown.LockedBy.Should().BeNull();
        unknown.LockedUntil.Should().BeNull();
        unknown.LastError.Should().BeNull();
        unknown.DeadAt.Should().BeNull();
        unknown.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_row_left_by_an_older_relay_is_claimed_by_a_relay_that_registers_it()
    {
        var root = new InMemoryDatabaseRoot();
        await using var older = BuildProvider(root, _ => { });
        await using var newer = BuildProvider(root, services => services.AddOutboxEventType<RelayNotification>(FutureEventType));
        var id = await SeedAsync(older, FutureEventType, OutboxTransports.InProcess);

        (await ClaimAsync(older, "old-relay")).Should().BeEmpty();
        var claimed = await ClaimAsync(newer, "new-relay");

        claimed.Should().ContainSingle(m => m.Id == id && m.LockedBy == "new-relay" && m.Attempts == 0);
    }

    [Fact]
    public async Task Event_type_matched_only_by_the_assembly_scan_is_still_claimed()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, _ => { });
        var id = await SeedAsync(provider, nameof(ScannedOnlyRelayNotification).ToUpperInvariant(), OutboxTransports.InProcess);

        (await ClaimAsync(provider, "relay")).Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Rows_on_other_transports_are_claimed_regardless_of_event_type()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, _ => { });
        var id = await SeedAsync(provider, FutureEventType, OutboxTransports.Temporal);

        (await ClaimAsync(provider, "relay")).Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Custom_resolver_disables_filtering()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, services =>
            services.AddSingleton(Mock.Of<IOutboxEventTypeResolver>()));
        var id = await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess);

        (await ClaimAsync(provider, "relay")).Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Known_event_type_is_dispatched_and_marked_processed_as_before()
    {
        var root = new InMemoryDatabaseRoot();
        var publisher = new Mock<IPublisher>();
        await using var provider = BuildProvider(root, services =>
        {
            services.AddOutboxEventType<RelayNotification>(RegisteredEventType);
            services.AddSingleton(publisher.Object);
        });
        var id = await SeedAsync(provider, RegisteredEventType, OutboxTransports.InProcess, payload: """{"name":"x"}""");

        var claimed = (await ClaimAsync(provider, "relay")).Single();
        var transport = provider.GetServices<IEventTransport>().Single(t => t.Name == OutboxTransports.InProcess);
        await transport.SendAsync(EventEnvelope.From(claimed));
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxStore>().MarkProcessedAsync(id, CancellationToken.None);

        publisher.Verify(p => p.Publish(It.Is<INotification>(n => n is RelayNotification && ((RelayNotification)n).Name == "x"),
            It.IsAny<CancellationToken>()), Times.Once);
        var row = await ReadAsync(provider, id);
        row.ProcessedAt.Should().NotBeNull();
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Unclaimable_event_types_are_listed_with_counts()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, services => services.AddOutboxEventType<RelayNotification>(RegisteredEventType));
        await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess);
        await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess);
        await SeedAsync(provider, "OutboxOtherFutureEvent", OutboxTransports.InProcess);
        await SeedAsync(provider, RegisteredEventType, OutboxTransports.InProcess);
        await SeedAsync(provider, "TemporalOnlyEvent", OutboxTransports.Temporal);

        await using var scope = provider.CreateAsyncScope();
        var types = await scope.ServiceProvider.GetRequiredService<OutboxStore>().ListUnclaimableEventTypesAsync(CancellationToken.None);

        types.Should().Equal(
            new OutboxUnclaimableEventType(FutureEventType, 2),
            new OutboxUnclaimableEventType("OutboxOtherFutureEvent", 1));
    }

    [Fact]
    public async Task Stalled_rows_are_warned_on_every_probe_and_published_as_a_gauge_until_drained()
    {
        var root = new InMemoryDatabaseRoot();
        await using var provider = BuildProvider(root, _ => { });
        var first = await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess);
        var second = await SeedAsync(provider, FutureEventType, OutboxTransports.InProcess);
        var logger = new CapturingLogger();
        using var gauge = new UnclaimableGauge();

        (await ReportAsync(provider, logger)).Should().Be(2);
        (await ReportAsync(provider, logger)).Should().Be(2);

        logger.Entries.Should().HaveCount(2).And.OnlyContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(FutureEventType) && e.Message.Contains(" 2 "));
        gauge.Read().Should().Be(2);

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
            await store.MarkProcessedAsync(first, CancellationToken.None);
            await store.MarkProcessedAsync(second, CancellationToken.None);
        }

        (await ReportAsync(provider, logger)).Should().Be(0);
        logger.Entries.Should().HaveCount(2);
        gauge.Read().Should().Be(0);
    }

    [Fact]
    public void Probe_runs_at_most_once_per_interval()
    {
        var catalog = new InProcessEventTypeCatalog(new OutboxEventTypeResolver([]), []);
        var now = DateTimeOffset.UtcNow;

        catalog.TryBeginProbe(now).Should().BeTrue();
        catalog.TryBeginProbe(now.AddSeconds(30)).Should().BeFalse();
        catalog.TryBeginProbe(now.AddMinutes(2)).Should().BeTrue();
    }

    private static async Task<long> ReportAsync(ServiceProvider provider, ILogger logger)
    {
        await using var scope = provider.CreateAsyncScope();
        return await OutboxUnclaimableReporter.ReportAsync(
            scope.ServiceProvider.GetRequiredService<OutboxStore>(), logger, "relay", CancellationToken.None);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class UnclaimableGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _value = -1;

        public UnclaimableGauge()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OutboxMetrics.MeterName && instrument.Name == "outbox.messages.unclaimable")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => _value = value);
            _listener.Start();
        }

        public long Read()
        {
            _listener.RecordObservableInstruments();
            return _value;
        }

        public void Dispose() => _listener.Dispose();
    }

    private static ServiceProvider BuildProvider(InMemoryDatabaseRoot root, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new TenantContext());
        services.AddDbContext<TestApplicationDbContext>(o => o.UseInMemoryDatabase("outbox-unknown", root));
        configure(services);
        services.AddTransactionalOutbox<TestApplicationDbContext>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedAsync(
        ServiceProvider provider, string eventType, string transport, int attempts = 0, string payload = "{}")
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        var row = new OutboxMessage
        {
            TenantId = "tenant-a",
            EventType = eventType,
            Transport = transport,
            EntityId = Guid.CreateVersion7(),
            Payload = payload,
            OccurredAt = past,
            NextAttemptAt = past,
            Attempts = attempts,
        };
        db.Set<OutboxMessage>().Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<List<OutboxMessage>> ClaimAsync(ServiceProvider provider, string node)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxStore>()
            .ClaimAsync(node, batchSize: 10, lease: TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static async Task<OutboxMessage> ReadAsync(ServiceProvider provider, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>()
            .Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id);
    }
}
