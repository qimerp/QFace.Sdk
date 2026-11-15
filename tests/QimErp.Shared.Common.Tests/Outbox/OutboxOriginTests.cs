using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QFace.Sdk.Temporal.Options;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Events;
using QimErp.Shared.Common.Interceptors;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Tests.Workflow;
using Xunit;

namespace QimErp.Shared.Common.Tests.Outbox;

public sealed class OutboxOriginTests
{
    private const string TenantId = "tenant-origin";
    private const string EventType = "OriginTestEvent";
    private static readonly InMemoryDatabaseRoot SharedRoot = new();

    [Theory]
    [InlineData("-local", "-local")]
    [InlineData(null, "")]
    public async Task IOutbox_stamps_rows_with_the_writers_task_queue_suffix(string? suffix, string expected)
    {
        await using var provider = BuildProvider(Guid.NewGuid().ToString(), suffix);

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IOutbox>()
                .Add(EventType, TenantId, Guid.CreateVersion7(), 0, new { Name = "x" });
            await scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>().SaveChangesAsync();
        }

        var row = await ReadAllAsync(provider);
        row.Should().ContainSingle().Which.Origin.Should().Be(expected);
    }

    [Fact]
    public async Task Suffixed_relay_claims_only_rows_of_its_own_origin()
    {
        var database = Guid.NewGuid().ToString();
        await using var local = BuildProvider(database, "-local");
        var own = await SeedAsync(local, "-local");
        await SeedAsync(local, "-local-pd");
        await SeedAsync(local, "");
        await SeedAsync(local, null);

        var claimed = await ClaimAsync(local, "laptop");

        claimed.Select(m => m.Id).Should().Equal(own);
    }

    [Fact]
    public async Task Unsuffixed_relay_claims_deployed_and_null_origin_rows_but_never_suffixed_rows()
    {
        var database = Guid.NewGuid().ToString();
        await using var deployed = BuildProvider(database, suffix: null);
        var empty = await SeedAsync(deployed, "");
        var legacy = await SeedAsync(deployed, null);
        await SeedAsync(deployed, "-local");
        await SeedAsync(deployed, "-local-pd");

        var claimed = await ClaimAsync(deployed, "contabo");

        claimed.Select(m => m.Id).Should().BeEquivalentTo([empty, legacy]);
    }

    [Fact]
    public async Task Null_origin_rows_are_never_claimed_by_a_suffixed_relay()
    {
        var database = Guid.NewGuid().ToString();
        await using var local = BuildProvider(database, "-local");
        await SeedAsync(local, null);

        (await ClaimAsync(local, "laptop")).Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_sweeper_processes_only_rows_of_its_own_origin()
    {
        var published = new List<object>();
        var database = Guid.NewGuid().ToString();
        await using var local = BuildProvider(database, "-local",
            services => services.AddScoped<IPublisher>(_ => new RecordingPublisher(published)));
        var own = await SeedSweeperRowAsync(local, "-local");
        await SeedSweeperRowAsync(local, null);
        await SeedSweeperRowAsync(local, "");
        var processor = new OutboxProcessorBackgroundService<TestApplicationDbContext>(
            local.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService<TestApplicationDbContext>>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OutboxProcessorOptions()));

        (await processor.ProcessPendingMessagesAsync(CancellationToken.None)).Should().Be(1);

        published.Should().ContainSingle();
        (await ReadAllAsync(local)).Where(m => m.ProcessedAt != null).Select(m => m.Id).Should().Equal(own);
    }

    public sealed record SweeperEvent(string Name) : IDomainEvent
    {
        public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
        public string TenantId { get; init; } = OutboxOriginTests.TenantId;
        public string? TriggeredBy { get; init; } = "tester";
    }

    private sealed class RecordingPublisher(List<object> published) : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default)
        {
            published.Add(notification);
            return Task.CompletedTask;
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            published.Add(notification!);
            return Task.CompletedTask;
        }
    }

    private static async Task<Guid> SeedSweeperRowAsync(ServiceProvider provider, string? origin)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>();
        var row = NewRow(origin);
        row.EventType = typeof(SweeperEvent).AssemblyQualifiedName!;
        row.Payload = OutboxEnvelope.Wrap(new SweeperEvent("x"), TenantId);
        db.OutboxMessages.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public void Postgres_claim_for_a_suffixed_relay_filters_on_its_origin_only()
    {
        var sql = RenderClaimSql(new OutboxOrigin("-local"), out var parameters);

        sql.Should().Contain("AND \"Origin\" = @p6").And.NotContain("IS NULL)");
        parameters["p6"].Should().Be("-local");
        parameters["p1"].Should().Be("node");
    }

    [Fact]
    public void Postgres_claim_for_the_deployed_relay_includes_null_origin_rows()
    {
        var sql = RenderClaimSql(OutboxOrigin.Deployed, out var parameters);

        sql.Should().Contain("AND (\"Origin\" = @p6 OR \"Origin\" IS NULL)");
        parameters["p6"].Should().Be(string.Empty);
    }

    private static string RenderClaimSql(OutboxOrigin origin, out Dictionary<string, object?> parameters)
    {
        var options = new DbContextOptionsBuilder<SqlContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new SqlContext(options);
        var claim = OutboxStore.ClaimSql(origin, "node", 10, TimeSpan.FromSeconds(30), ["x"]);
        parameters = claim.GetArguments()
            .Select((value, index) => (Name: $"p{index}", Value: value))
            .ToDictionary(p => p.Name, p => p.Value);
        return db.Set<OutboxMessage>().FromSql(claim).ToQueryString();
    }

    private sealed class SqlContext(DbContextOptions<SqlContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OutboxMessage>().ToTable("OutboxMessages");
    }

    [Fact]
    public void Origin_prefers_bound_temporal_options_over_raw_configuration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration("-from-config"));
        services.Configure<TemporalOptions>(o => o.TaskQueueSuffix = "-from-options");

        OutboxOrigin.FromServices(services.BuildServiceProvider()).Value.Should().Be("-from-options");
    }

    [Fact]
    public void Origin_falls_back_to_configuration_and_is_deployed_when_absent()
    {
        var withConfig = new ServiceCollection().AddSingleton<IConfiguration>(Configuration("-local-pd"));
        OutboxOrigin.FromServices(withConfig.BuildServiceProvider()).Value.Should().Be("-local-pd");

        var none = OutboxOrigin.FromServices(new ServiceCollection().BuildServiceProvider());
        none.IsDeployed.Should().BeTrue();
        none.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Interceptor_stamps_rows_added_directly_without_an_origin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new OutboxOrigin("-local"));
        await using var sp = services.BuildServiceProvider();
        var interceptor = new AuditEntitySaveChangesInterceptor(
            new FakeCurrentUserService(TenantId),
            NullLogger<AuditEntitySaveChangesInterceptor>.Instance,
            sp);
        var options = new DbContextOptionsBuilder<TestApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new TestApplicationDbContext(options, new TenantContext());
        var unstamped = NewRow(origin: null);
        var stamped = NewRow(origin: "-other");
        db.OutboxMessages.AddRange(unstamped, stamped);

        await db.SaveChangesAsync();

        var rows = await db.OutboxMessages.AsNoTracking().ToDictionaryAsync(m => m.Id);
        rows[unstamped.Id].Origin.Should().Be("-local");
        rows[stamped.Id].Origin.Should().Be("-other");
    }

    private static IConfiguration Configuration(string? suffix) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Temporal:TaskQueueSuffix"] = suffix })
            .Build();

    private static ServiceProvider BuildProvider(
        string database,
        string? suffix,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = Configuration(suffix);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<ITenantContext>(new TenantContext());
        services.AddDbContext<TestApplicationDbContext>(o => o.UseInMemoryDatabase(database, SharedRoot));
        services.AddTransactionalOutbox<TestApplicationDbContext>(configuration);
        services.Configure<OutboxOptions>(o => o.DefaultTransports = [OutboxTransports.Temporal]);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static OutboxMessage NewRow(string? origin)
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new OutboxMessage
        {
            TenantId = TenantId,
            EventType = EventType,
            Transport = OutboxTransports.Temporal,
            EntityId = Guid.CreateVersion7(),
            Payload = "{}",
            OccurredAt = past,
            NextAttemptAt = past,
            Origin = origin,
        };
    }

    private static async Task<Guid> SeedAsync(ServiceProvider provider, string? origin)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>();
        var row = NewRow(origin);
        db.OutboxMessages.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<List<OutboxMessage>> ClaimAsync(ServiceProvider provider, string node)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxStore>()
            .ClaimAsync(node, batchSize: 10, lease: TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static async Task<List<OutboxMessage>> ReadAllAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>()
            .OutboxMessages.AsNoTracking().ToListAsync();
    }
}
