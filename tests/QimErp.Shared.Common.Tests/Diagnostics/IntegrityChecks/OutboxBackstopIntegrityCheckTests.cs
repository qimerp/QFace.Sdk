using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QimErp.Shared.Common.Diagnostics.IntegrityChecks;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Services.MultiTenancy;
using Xunit;

namespace QimErp.Shared.Common.Tests.Diagnostics.IntegrityChecks;

public sealed class OutboxBackstopIntegrityCheckTests
{
    private static TestApplicationDbContext CreateContext()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant("tenant-integrity");

        var options = new DbContextOptionsBuilder<TestApplicationDbContext>()
            .UseInMemoryDatabase($"integrity-{Guid.NewGuid()}")
            .Options;

        return new TestApplicationDbContext(options, tenantContext);
    }

    private static OutboxMessage Stale(TimeSpan age) => new()
    {
        EventType = "Test.Event",
        Payload = "{}",
        IsProcessed = false,
        OccurredAt = DateTime.UtcNow - age
    };

    [Fact(DisplayName = "No unprocessed OutboxMessage rows => Clean result with zero count")]
    public async Task NoRows_ReturnsClean()
    {
        await using var context = CreateContext();
        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context);

        var result = await check.RunAsync(CancellationToken.None);

        result.HasFindings.Should().BeFalse();
        result.Count.Should().Be(0);
        result.CheckName.Should().Be(OutboxBackstopIntegrityCheck<TestApplicationDbContext>.CheckName);
    }

    [Fact(DisplayName = "Unprocessed row younger than the stale threshold is not counted")]
    public async Task RowYoungerThanThreshold_IsNotCounted()
    {
        await using var context = CreateContext();
        context.OutboxMessages.Add(Stale(TimeSpan.FromMinutes(30)));
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context, TimeSpan.FromHours(1));
        var result = await check.RunAsync(CancellationToken.None);

        result.HasFindings.Should().BeFalse();
        result.Count.Should().Be(0);
    }

    [Fact(DisplayName = "Unprocessed row older than the stale threshold is counted with its Id as a sample")]
    public async Task RowOlderThanThreshold_IsCounted_WithSampleId()
    {
        await using var context = CreateContext();
        var stuck = Stale(TimeSpan.FromHours(2));
        context.OutboxMessages.Add(stuck);
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context, TimeSpan.FromHours(1));
        var result = await check.RunAsync(CancellationToken.None);

        result.HasFindings.Should().BeTrue();
        result.Count.Should().Be(1);
        result.SampleIds.Should().ContainSingle().Which.Should().Be(stuck.Id.ToString());
    }

    [Fact(DisplayName = "Processed rows are never counted, no matter how old")]
    public async Task ProcessedRow_IsNeverCounted()
    {
        await using var context = CreateContext();
        var processed = Stale(TimeSpan.FromDays(1));
        processed.IsProcessed = true;
        context.OutboxMessages.Add(processed);
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context, TimeSpan.FromHours(1));
        var result = await check.RunAsync(CancellationToken.None);

        result.HasFindings.Should().BeFalse();
        result.Count.Should().Be(0);
    }

    [Fact(DisplayName = "Count reflects every stuck row even when sample ids are capped at 10")]
    public async Task CountReflectsAllRows_SampleIdsCappedAt10()
    {
        await using var context = CreateContext();
        for (var i = 0; i < 15; i++)
            context.OutboxMessages.Add(Stale(TimeSpan.FromHours(2)));
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context, TimeSpan.FromHours(1));
        var result = await check.RunAsync(CancellationToken.None);

        result.Count.Should().Be(15);
        result.SampleIds.Should().HaveCount(10);
    }

    [Fact(DisplayName = "Default stale threshold is one hour when none is supplied")]
    public async Task DefaultThreshold_IsOneHour()
    {
        await using var context = CreateContext();
        context.OutboxMessages.Add(Stale(TimeSpan.FromMinutes(50)));
        var stuck = Stale(TimeSpan.FromMinutes(90));
        context.OutboxMessages.Add(stuck);
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context);
        var result = await check.RunAsync(CancellationToken.None);

        result.Count.Should().Be(1);
        result.SampleIds.Should().ContainSingle().Which.Should().Be(stuck.Id.ToString());
    }

    [Fact(DisplayName = "Stuck rows of other origins are reported as not owned, never in the count")]
    public async Task OtherOriginRows_AreReportedSeparately()
    {
        await using var context = CreateContext();
        var own = Stale(TimeSpan.FromHours(2));
        own.Origin = "-local";
        var deployed = Stale(TimeSpan.FromHours(2));
        deployed.Origin = string.Empty;
        var legacy = Stale(TimeSpan.FromHours(2));
        var otherLaptop = Stale(TimeSpan.FromHours(2));
        otherLaptop.Origin = "-local-pd";
        context.OutboxMessages.AddRange(own, deployed, legacy, otherLaptop);
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(
            context, TimeSpan.FromHours(1), new OutboxOrigin("-local"));
        var result = await check.RunAsync(CancellationToken.None);

        result.Count.Should().Be(1);
        result.SampleIds.Should().Equal(own.Id.ToString());
        result.NotOwnedCounts.Should().BeEquivalentTo(new Dictionary<string, long> { [""] = 2, ["-local-pd"] = 1 });
    }

    [Fact(DisplayName = "The deployed origin owns empty and null origin rows")]
    public async Task DeployedOrigin_OwnsEmptyAndNullRows()
    {
        await using var context = CreateContext();
        var deployed = Stale(TimeSpan.FromHours(2));
        deployed.Origin = string.Empty;
        var legacy = Stale(TimeSpan.FromHours(2));
        var laptop = Stale(TimeSpan.FromHours(2));
        laptop.Origin = "-local";
        context.OutboxMessages.AddRange(deployed, legacy, laptop);
        await context.SaveChangesAsync();

        var check = new OutboxBackstopIntegrityCheck<TestApplicationDbContext>(context, TimeSpan.FromHours(1));
        var result = await check.RunAsync(CancellationToken.None);

        result.Count.Should().Be(2);
        result.SampleIds.Should().BeEquivalentTo([deployed.Id.ToString(), legacy.Id.ToString()]);
        result.NotOwnedCounts.Should().BeEquivalentTo(new Dictionary<string, long> { ["-local"] = 1 });
    }

    [Fact(DisplayName = "DI-built check uses the registered outbox origin")]
    public async Task DiBuiltCheck_UsesRegisteredOrigin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Temporal:TaskQueueSuffix"] = "-local" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITenantContext>(new TenantContext());
        services.AddDbContext<TestApplicationDbContext>(o => o.UseInMemoryDatabase($"integrity-{Guid.NewGuid()}"));
        services.AddTransactionalOutbox<TestApplicationDbContext>(configuration);
        services.AddIntegrityCheck<OutboxBackstopIntegrityCheck<TestApplicationDbContext>>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestApplicationDbContext>();
        context.OutboxMessages.Add(Stale(TimeSpan.FromHours(2)));
        await context.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetServices<IIntegrityCheck>().Single().RunAsync(CancellationToken.None);

        result.Count.Should().Be(0);
        result.NotOwnedCounts.Should().BeEquivalentTo(new Dictionary<string, long> { [""] = 1 });
    }
}
