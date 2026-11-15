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
using QimErp.Shared.Common.Workflow.Entities;
using Xunit;
using RedisCacheService = QimErp.Shared.Common.Services.Cache.RedisCacheService;

namespace QimErp.Shared.Common.Testing.Tests;

public class WorkflowCacheTestSupportTests
{
    private const string TenantId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    private const string OtherTenantId = "ffffffff-ffff-ffff-ffff-ffffffffffff";

    [Fact]
    public async Task SeededMarker_IsVersion2_AndKeepsListedPairsBound()
    {
        var shared = SharedCache(new InMemoryGlobalRedis(), enabled: true);
        var configCache = new WorkflowConfigCacheService(shared, new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

        await WorkflowCacheTestSupport.SeedTenantWarmMarkerAsync(shared, TenantId, ["HR:Employee"]);

        (await configCache.GetTenantWarmStatusAsync("HR", "Leave", TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
        (await configCache.GetTenantWarmStatusAsync("HR", "Employee", TenantId)).Should().Be(WorkflowCacheWarmStatus.BoundAtWarm);
        (await configCache.GetTenantWarmStatusAsync("Leave", "Employee", TenantId)).Should().Be(WorkflowCacheWarmStatus.BoundAtWarm);
        (await configCache.GetTenantWarmStatusAsync("HR", "Leave", OtherTenantId)).Should().Be(WorkflowCacheWarmStatus.NotWarm);
    }

    [Fact]
    public async Task Decorator_ReportsListedTenantsWarm_KeepsBoundPairs_AndForwardsEverythingElse()
    {
        var config = new EntityWorkflowConfig { EnableWorkflowForCreate = true };
        var inner = new StubConfigCache(WorkflowConfigLookup.Found(config), WorkflowCacheWarmStatus.NotWarm);
        IWorkflowConfigCacheService decorator = new WarmTenantsWorkflowConfigCache(inner, [TenantId], ["HR:Employee"]);

        (await decorator.GetTenantWarmStatusAsync("HR", "Leave", TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
        (await decorator.GetTenantWarmStatusAsync("HR", "Employee", TenantId)).Should().Be(WorkflowCacheWarmStatus.BoundAtWarm);
        (await decorator.GetTenantWarmStatusAsync("Leave", "employee", TenantId)).Should().Be(WorkflowCacheWarmStatus.BoundAtWarm);
        (await decorator.GetTenantWarmStatusAsync("HR", "Leave", OtherTenantId)).Should().Be(WorkflowCacheWarmStatus.NotWarm);
        (await decorator.LookupEntityConfigAsync("HR", "Employee", TenantId)).Config.Should().BeSameAs(config);
    }

    [Fact]
    public void AddWarmWorkflowTenants_KeepsLifetimeAndWrapsRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISharedDistributedCacheService>(SharedCache(new InMemoryGlobalRedis(), enabled: true));
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

        (await gate.GetApprovalStateAsync(TenantId, "HR", "Employee", "CREATE")).Should().Be(expected);
    }

    private static SharedRedisCacheService SharedCache(InMemoryGlobalRedis redis, bool enabled) =>
        new(redis, NullLogger<RedisCacheService>.Instance, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCache:Enabled"] = enabled.ToString() })
            .Build());

    private sealed class StubConfigCache(WorkflowConfigLookup lookup, WorkflowCacheWarmStatus warm) : IWorkflowConfigCacheService
    {
        public Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(warm);

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

internal sealed class InMemoryGlobalRedis : IGlobalRedisCacheService
{
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    public Task<T> GetAsync<T>(string key) =>
        Task.FromResult(_store.TryGetValue(key, out var value) ? (T)value! : default!);

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key) => Task.FromResult(_store.ContainsKey(key));
    public Task<bool> RemoveAsync(string key) => Task.FromResult(_store.Remove(key));
    public Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys) => throw new NotSupportedException();
    public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task RemoveManyAsync(params string[] keys) => throw new NotSupportedException();
    public Task<long> RemoveByPatternAsync(string pattern) => throw new NotSupportedException();
    public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task<TimeSpan?> GetTimeToLiveAsync(string key) => throw new NotSupportedException();
    public Task<bool> ExtendExpirationAsync(string key, TimeSpan expiration) => throw new NotSupportedException();
    public Task<T> HashGetAsync<T>(string key, string field) => throw new NotSupportedException();
    public Task<bool> HashSetAsync<T>(string key, string field, T value) => throw new NotSupportedException();
    public Task<Dictionary<string, T>> HashGetAllAsync<T>(string key) => throw new NotSupportedException();
    public Task<long> ListPushAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<T> ListPopAsync<T>(string key) => throw new NotSupportedException();
    public Task<List<T>> ListGetRangeAsync<T>(string key, long start, long stop) => throw new NotSupportedException();
    public Task<bool> SetAddAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<bool> SetContainsAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<List<T>> SetGetAllAsync<T>(string key) => throw new NotSupportedException();
}
