using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QimErp.Shared.Common.Database.Configurations;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Services.MultiTenancy;
using Xunit;

namespace QimErp.Shared.Common.Tests.Outbox;

public sealed class OutboxSchemaStartupCheckTests
{
    private const string UnusedPostgres = "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public async Task InMemory_host_skips_the_check()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TenantContext());
        services.AddDbContext<TestApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<OutboxSchemaStartupCheck<TestApplicationDbContext>>();
        var check = new OutboxSchemaStartupCheck<TestApplicationDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(), logger)
        {
            Probe = (_, _, _) => throw new InvalidOperationException("must not probe InMemory"),
        };

        await check.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_origin_column_logs_critical_and_fails_startup()
    {
        var (check, logger) = Create<PublicContext>((_, _, _) => Task.FromResult(OutboxSchemaState.ColumnMissing));

        var act = () => check.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("\"Origin\"").And.Contain("AddOutboxOrigin").And.Contain("42703");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical && e.Message == thrown.Which.Message);
    }

    [Fact]
    public async Task Missing_outbox_table_fails_startup()
    {
        var (check, logger) = Create<PublicContext>((_, _, _) => Task.FromResult(OutboxSchemaState.TableMissing));

        var act = () => check.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("does not exist");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Ready_schema_starts_without_logging()
    {
        var (check, logger) = Create<PublicContext>((_, _, _) => Task.FromResult(OutboxSchemaState.Ready));

        await check.StartAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_failure_is_logged_critical_and_rethrown()
    {
        var failure = new InvalidOperationException("connection refused");
        var (check, logger) = Create<PublicContext>((_, _, _) => Task.FromException<OutboxSchemaState>(failure));

        var act = () => check.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical && e.Exception == failure);
    }

    [Fact]
    public async Task Probe_uses_the_mapped_schema_and_table()
    {
        OutboxTableName? seen = null;
        var (check, _) = Create<ReportingContext>((_, table, _) =>
        {
            seen = table;
            return Task.FromResult(OutboxSchemaState.Ready);
        });

        await check.StartAsync(CancellationToken.None);

        seen.Should().Be(new OutboxTableName("reporting", "OutboxMessages"));
    }

    [Fact]
    public void Probe_sql_checks_the_origin_column_in_the_mapped_schema()
    {
        using var db = new ReportingContext(new DbContextOptionsBuilder<ReportingContext>().UseNpgsql(UnusedPostgres).Options);
        var sql = OutboxSchemaStartupCheck<ReportingContext>.ProbeSql(new OutboxTableName("reporting", "OutboxMessages"));

        var text = db.Database.SqlQuery<OutboxSchemaProbeRow>(sql).ToQueryString();

        text.Should().Contain("information_schema.columns").And.Contain("current_schema()");
        sql.GetArguments().Should().Equal("reporting", "OutboxMessages", "reporting", "OutboxMessages", "Origin");
    }

    [Fact]
    public void AddTransactionalOutbox_registers_the_check_before_the_relay()
    {
        var services = new ServiceCollection();
        services.AddTransactionalOutbox<PublicContext>();

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        hosted.IndexOf(typeof(OutboxSchemaStartupCheck<PublicContext>)).Should()
            .BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(hosted.IndexOf(typeof(OutboxRelayHostedService)));
    }

    private static (OutboxSchemaStartupCheck<TContext> Check, CapturingLogger<OutboxSchemaStartupCheck<TContext>> Logger)
        Create<TContext>(Func<TContext, OutboxTableName, CancellationToken, Task<OutboxSchemaState>> probe)
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddDbContext<TContext>(o => o.UseNpgsql(UnusedPostgres));
        var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<OutboxSchemaStartupCheck<TContext>>();
        return (new OutboxSchemaStartupCheck<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), logger)
        {
            Probe = probe,
        }, logger);
    }

    private sealed class PublicContext(DbContextOptions<PublicContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }

    private sealed class ReportingContext(DbContextOptions<ReportingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("reporting");
            modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
