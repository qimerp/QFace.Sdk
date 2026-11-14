using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QFace.Sdk.RedisCache.Services;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.Testing;
using QimErp.Shared.Common.Tests.TenantSetup;
using QimErp.Shared.Common.Tests.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using Xunit;
using RedisCacheService = QimErp.Shared.Common.Services.Cache.RedisCacheService;

namespace QimErp.Shared.Common.Tests.Testing;

public class WorkflowCacheTestSupportTests
{
    private const string TenantId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    private const string OtherTenantId = "ffffffff-ffff-ffff-ffff-ffffffffffff";

    [Fact]
    public async Task SeedTenantWarmMarker_MakesRealConfigCacheReportWarm()
    {
        var shared = new SharedRedisCacheService(
            new InMemoryGlobalRedis(), NullLogger<RedisCacheService>.Instance, new ConfigurationBuilder().Build());
        var configCache = new WorkflowConfigCacheService(shared, new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

        await WorkflowCacheTestSupport.SeedTenantWarmMarkerAsync(shared, TenantId);

        (await configCache.GetTenantWarmStatusAsync(TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
        (await configCache.GetTenantWarmStatusAsync(OtherTenantId)).Should().Be(WorkflowCacheWarmStatus.NotWarm);
    }

    [Fact]
    public async Task SeedTenantWarmMarker_WorksWithInMemoryTestCache()
    {
        var cache = new InMemoryDistributedCacheService();
        var configCache = new WorkflowConfigCacheService(cache, new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

        await WorkflowCacheTestSupport.SeedTenantWarmMarkerAsync(cache, TenantId);

        (await configCache.GetTenantWarmStatusAsync(TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
    }

    [Fact]
    public async Task Decorator_ReportsListedTenantsWarm_AndForwardsEverythingElse()
    {
        var config = new EntityWorkflowConfig { EnableWorkflowForCreate = true };
        var inner = new StubConfigCache(WorkflowConfigLookup.Found(config), WorkflowCacheWarmStatus.NotWarm);
        IWorkflowConfigCacheService decorator = new WarmTenantsWorkflowConfigCache(inner, [TenantId]);

        (await decorator.GetTenantWarmStatusAsync(TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
        (await decorator.GetTenantWarmStatusAsync(OtherTenantId)).Should().Be(WorkflowCacheWarmStatus.NotWarm);
        (await decorator.LookupEntityConfigAsync("HR", "Employee", TenantId)).Config.Should().BeSameAs(config);
    }

    [Fact]
    public void AddWarmWorkflowTenants_KeepsLifetimeAndWrapsRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISharedDistributedCacheService>(new InMemoryDistributedCacheService());
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddLogging();
        services.AddScoped<IWorkflowConfigCacheService, WorkflowConfigCacheService>();

        services.AddWarmWorkflowTenants(TenantId);

        services.Single(d => d.ServiceType == typeof(IWorkflowConfigCacheService)).Lifetime.Should().Be(ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWorkflowConfigCacheService>().Should().BeOfType<WarmTenantsWorkflowConfigCache>();
    }

    [Fact]
    public void AddWarmWorkflowTenants_ThrowsWhenNothingRegistered()
    {
        var act = () => new ServiceCollection().AddWarmWorkflowTenants(TenantId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(true, WorkflowApprovalGateState.Off)]
    [InlineData(false, WorkflowApprovalGateState.Unavailable)]
    public async Task DisabledCache_GateSavesWithoutApprovalOnlyForWarmedTenants(bool warm, WorkflowApprovalGateState expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCache:Enabled"] = "false" })
            .Build());
        services.AddSingleton<IGlobalRedisCacheService>(new InMemoryGlobalRedis());
        services.AddScoped<ISharedDistributedCacheService, SharedRedisCacheService>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<ITenantModuleAccessService, TenantModuleAccessService>();
        services.AddScoped<IWorkflowConfigCacheService, WorkflowConfigCacheService>();
        services.AddScoped<IWorkflowModuleApprovalGate, WorkflowModuleApprovalGate>();
        if (warm)
            services.AddWarmWorkflowTenants(TenantId);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IWorkflowModuleApprovalGate>();

        (await gate.GetApprovalStateAsync(TenantId, "HR", "Employee")).Should().Be(expected);
    }

    private sealed class StubConfigCache(WorkflowConfigLookup lookup, WorkflowCacheWarmStatus warm) : IWorkflowConfigCacheService
    {
        public Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string? tenantId = null) => Task.FromResult(warm);

        public Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(lookup);

        public Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(lookup.Config);

        public Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(false);

        public Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult<string?>(null);

        public Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
            string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(new List<WorkflowTriggerCondition>());
    }
}
