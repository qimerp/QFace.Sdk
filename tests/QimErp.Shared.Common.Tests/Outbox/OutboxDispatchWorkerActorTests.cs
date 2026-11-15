using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Event;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QimErp.Shared.Common.Database.Configurations;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Outbox.Actors;
using Xunit;

namespace QimErp.Shared.Common.Tests.Outbox;

public sealed class OutboxDispatchWorkerActorTests
{
    private static readonly InMemoryDatabaseRoot SharedRoot = new();

    [Fact]
    public async Task Failed_delivery_is_never_marked_processed()
    {
        await using var harness = new Harness();
        var claimed = await harness.SeedAndClaimAsync();

        harness.Dispatch(
            new StubTransport(new DbUpdateException("An error occurred while saving the entity changes.")), claimed);
        var row = await harness.WaitForRowAsync(claimed.Id, r => r.Attempts > 0);

        row.ProcessedAt.Should().BeNull();
        row.Attempts.Should().Be(1);
        row.LastError.Should().Contain("saving the entity changes");
        row.LockedBy.Should().BeNull();
        row.DeadAt.Should().BeNull();
    }

    [Fact]
    public async Task Failed_delivery_on_the_last_attempt_is_dead_not_processed()
    {
        await using var harness = new Harness(maxAttempts: 1);
        var claimed = await harness.SeedAndClaimAsync();

        harness.Dispatch(new StubTransport(new InvalidOperationException("boom")), claimed);
        var row = await harness.WaitForRowAsync(claimed.Id, r => r.DeadAt != null);

        row.ProcessedAt.Should().BeNull();
        row.Attempts.Should().Be(1);
        row.LastError.Should().Be("System.InvalidOperationException: boom");
    }

    [Fact]
    public async Task Successful_delivery_is_marked_processed_without_an_error()
    {
        await using var harness = new Harness();
        var claimed = await harness.SeedAndClaimAsync();

        harness.Dispatch(new StubTransport(null), claimed);
        var row = await harness.WaitForRowAsync(claimed.Id, r => r.ProcessedAt != null);

        row.Attempts.Should().Be(0);
        row.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Successful_redelivery_clears_the_previous_error()
    {
        await using var harness = new Harness();
        var claimed = await harness.SeedAndClaimAsync(lastError: "System.TimeoutException: earlier attempt");

        harness.Dispatch(new StubTransport(null), claimed);
        var row = await harness.WaitForRowAsync(claimed.Id, r => r.ProcessedAt != null);

        row.LastError.Should().BeNull();
        row.LockedBy.Should().BeNull();
    }

    [Fact]
    public async Task Recorded_error_is_truncated_to_2000_characters()
    {
        await using var harness = new Harness();
        var claimed = await harness.SeedAndClaimAsync();

        harness.Dispatch(new StubTransport(new InvalidOperationException(new string('x', 5000))), claimed);
        var row = await harness.WaitForRowAsync(claimed.Id, r => r.Attempts > 0);

        row.LastError.Should().HaveLength(2000).And.StartWith("System.InvalidOperationException: xxx");
    }

    [Fact]
    public async Task Delivery_failure_is_logged_even_when_recording_the_retry_fails()
    {
        await using var harness = new Harness();
        var claimed = await harness.SeedAndClaimAsync();
        var failure = new InvalidOperationException("transport down");
        harness.Saves.Fail = true;

        harness.Dispatch(new StubTransport(failure), claimed);
        var warning = await harness.WaitForLogAsync<Warning>(e => e.Cause == failure);

        warning.Message.ToString().Should().Contain("outbox.retry").And.Contain(claimed.Id.ToString());
        var row = await harness.ReadAsync(claimed.Id);
        row.ProcessedAt.Should().BeNull();
        row.Attempts.Should().Be(0);
        row.LastError.Should().BeNull();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ActorSystem _system = ActorSystem.Create($"outbox-dispatch-{Guid.NewGuid():N}");
        private readonly ConcurrentQueue<LogEvent> _logs = new();

        public Harness(int maxAttempts = 10)
        {
            var database = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Saves);
            services.AddDbContext<DispatchContext>(o => o.UseInMemoryDatabase(database, SharedRoot));
            services.AddTransactionalOutbox<DispatchContext>();
            services.Configure<OutboxOptions>(o => o.MaxAttempts = maxAttempts);
            _provider = services.BuildServiceProvider();

            var collector = _system.ActorOf(Props.Create(() => new LogCollector(_logs)));
            _system.EventStream.Subscribe(collector, typeof(LogEvent));
        }

        public SaveSwitch Saves { get; } = new();

        public async Task<OutboxMessage> SeedAndClaimAsync(string? lastError = null)
        {
            await using (var scope = _provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DispatchContext>();
                var past = DateTimeOffset.UtcNow.AddMinutes(-1);
                db.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    TenantId = "tenant-dispatch",
                    EventType = "DispatchTestEvent",
                    Transport = OutboxTransports.Temporal,
                    EntityId = Guid.CreateVersion7(),
                    Payload = "{}",
                    OccurredAt = past,
                    NextAttemptAt = past,
                    Origin = string.Empty,
                    LastError = lastError,
                });
                await db.SaveChangesAsync();
            }

            await using var claimScope = _provider.CreateAsyncScope();
            return (await claimScope.ServiceProvider.GetRequiredService<OutboxStore>()
                .ClaimAsync("node", 10, TimeSpan.FromSeconds(30), CancellationToken.None)).Single();
        }

        public void Dispatch(IEventTransport transport, OutboxMessage claimed)
        {
            var transports = new Dictionary<string, IEventTransport>(StringComparer.OrdinalIgnoreCase)
            {
                [transport.Name] = transport,
            };
            var scopes = _provider.GetRequiredService<IServiceScopeFactory>();
            var options = _provider.GetRequiredService<IOptions<OutboxOptions>>();
            var worker = _system.ActorOf(Props.Create(() => new OutboxDispatchWorkerActor(scopes, transports, options)));
            worker.Tell(new OutboxDispatch(claimed));
        }

        public async Task<OutboxMessage> WaitForRowAsync(Guid id, Func<OutboxMessage, bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var row = await ReadAsync(id);
                if (done(row))
                    return row;
                await Task.Delay(25);
            }

            throw new TimeoutException("Dispatch did not finish.");
        }

        public async Task<TEvent> WaitForLogAsync<TEvent>(Func<TEvent, bool> match) where TEvent : LogEvent
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var found = _logs.OfType<TEvent>().FirstOrDefault(match);
                if (found is not null)
                    return found;
                await Task.Delay(25);
            }

            throw new TimeoutException($"No matching {typeof(TEvent).Name} was logged.");
        }

        public async Task<OutboxMessage> ReadAsync(Guid id)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DispatchContext>()
                .Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id);
        }

        public async ValueTask DisposeAsync()
        {
            await _system.Terminate();
            await _provider.DisposeAsync();
        }
    }

    private sealed class SaveSwitch
    {
        public volatile bool Fail;
    }

    private sealed class DispatchContext(DbContextOptions<DispatchContext> options, SaveSwitch saves) : DbContext(options)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
            saves.Fail
                ? throw new DbUpdateException("Simulated failure while recording the retry.")
                : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }

    private sealed class LogCollector : ReceiveActor
    {
        public LogCollector(ConcurrentQueue<LogEvent> sink) => Receive<LogEvent>(sink.Enqueue);
    }

    private sealed class StubTransport(Exception? failure) : IEventTransport
    {
        public string Name => OutboxTransports.Temporal;

        public Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default) =>
            failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
}
