using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QFace.Sdk.AI.Models;
using QFace.Sdk.RedisCache.Extensions;
using QFace.Sdk.RedisCache.Services.Providers;
using QimErp.Shared.Common.Constants;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Integrations.GoogleWorkspace;
using QimErp.Shared.Common.Integrations.Microsoft365;
using QimErp.Shared.Common.Services.AI;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.TenantSetup;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using Xunit;

namespace QimErp.Shared.Common.Tests.Cache;

public class SharedCacheKeyPrefixTests
{
    private const string TenantId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    [Fact]
    public async Task PrefixedService_ReadsModuleSnapshotWrittenByUnprefixedPlatform()
    {
        var redis = new InMemoryRedisProvider();
        using var platform = BuildService(redis, keyPrefix: "");
        using var coreHr = BuildService(redis, keyPrefix: "corehr:");

        await Shared(platform).SetAsync(
            SharedCacheKeys.TenantModuleSnapshot(Guid.Parse(TenantId)),
            new TenantModuleSnapshotEntry(1, [ModuleKeys.CoreHR, ModuleKeys.Workflow]),
            TimeSpan.FromDays(365));

        var access = new TenantModuleAccessService(Shared(coreHr), new HttpContextAccessor());

        (await access.IsModuleEnabledAsync(TenantId, ModuleKeys.Workflow)).Should().BeTrue();
        redis.Keys.Should().Contain(SharedCacheKeys.TenantModuleSnapshot(Guid.Parse(TenantId)));
        redis.Keys.Should().NotContain(k => k.StartsWith("corehr:"));
    }

    [Fact]
    public async Task PrefixedService_ReadsWorkflowConfigWrittenByUnprefixedPlatform()
    {
        var redis = new InMemoryRedisProvider();
        using var platform = BuildService(redis, keyPrefix: "");
        using var finance = BuildService(redis, keyPrefix: "accounting:");

        await Shared(platform).SetAsync(
            WorkflowDefinitionCacheKeys.Configuration(TenantId, "Accounting", "Invoice"),
            new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "INV" },
            TimeSpan.FromDays(1));

        var configCache = new WorkflowConfigCacheService(
            Shared(finance), new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

        (await configCache.GetWorkflowCodeAsync("Accounting", "Invoice", "CREATE", TenantId)).Should().Be("INV");
    }

    [Fact]
    public async Task PrefixedService_ReadsPlatformAIConfigWrittenByUnprefixedPlatform()
    {
        var redis = new InMemoryRedisProvider();
        using var platform = BuildService(redis, keyPrefix: "");
        using var coreHr = BuildService(redis, keyPrefix: "corehr:");
        var seeded = new AIOptions();
        seeded.Anthropic.ApiKey = "platform-key";

        await Shared(platform).SetAsync(SharedCacheKeys.PlatformAIProviderConfig(), seeded, TimeSpan.FromDays(365));

        var provider = new CachedAIOptionsProvider(
            Microsoft.Extensions.Options.Options.Create(new AIOptions()),
            coreHr.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachedAIOptionsProvider>.Instance);

        (await provider.GetOptionsAsync()).Anthropic.ApiKey.Should().Be("platform-key");
    }

    [Fact]
    public async Task PrivateKeys_StayNamespacedPerService()
    {
        var redis = new InMemoryRedisProvider();
        using var coreHr = BuildService(redis, keyPrefix: "corehr:");
        using var finance = BuildService(redis, keyPrefix: "accounting:");
        var privateKey = $"qface:qimerp:{TenantId}:hr:employee_1";

        await Private(coreHr).SetAsync(privateKey, "corehr-value", TimeSpan.FromMinutes(5));

        redis.Keys.Should().Equal($"corehr:{privateKey}");
        (await Private(finance).GetAsync<string>(privateKey)).Should().BeNull();
        (await Private(coreHr).GetAsync<string>(privateKey)).Should().Be("corehr-value");
    }

    [Theory]
    [InlineData(typeof(TenantModuleAccessService))]
    [InlineData(typeof(TenantPluginAccessService))]
    [InlineData(typeof(WorkflowConfigCacheService))]
    [InlineData(typeof(WorkflowDefinitionProvider))]
    [InlineData(typeof(WorkflowDefinitionCacheWriter))]
    [InlineData(typeof(Microsoft365GraphClient))]
    [InlineData(typeof(GoogleCalendarClient))]
    public void SharedKeyConsumers_DependOnSharedCacheOnly(Type consumer)
    {
        var cacheParameters = consumer
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => typeof(ICacheService).IsAssignableFrom(t))
            .ToList();

        cacheParameters.Should().NotBeEmpty();
        cacheParameters.Should().OnlyContain(t => t == typeof(ISharedDistributedCacheService));
    }

    private static ServiceProvider BuildService(InMemoryRedisProvider redis, string keyPrefix)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCache:KeyPrefix"] = keyPrefix })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRedisCache(configuration);
        services.AddSingleton<IRedisProvider>(redis);
        services.AddScoped<IDistributedCacheService, RedisCacheService>();
        services.AddScoped<ISharedDistributedCacheService, SharedRedisCacheService>();
        return services.BuildServiceProvider();
    }

    private static ISharedDistributedCacheService Shared(ServiceProvider sp) =>
        sp.GetRequiredService<ISharedDistributedCacheService>();

    private static IDistributedCacheService Private(ServiceProvider sp) =>
        sp.GetRequiredService<IDistributedCacheService>();

    private sealed class InMemoryRedisProvider : IRedisProvider
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _store.Keys;

        public Task<T> GetAsync<T>(string key) =>
            Task.FromResult(_store.TryGetValue(key, out var v) ? (T)v! : default!);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key) => Task.FromResult(_store.Remove(key));
        public Task<bool> ExistsAsync(string key) => Task.FromResult(_store.ContainsKey(key));
        public Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys) => throw new NotSupportedException();
        public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null) => throw new NotSupportedException();
        public Task RemoveManyAsync(params string[] keys) => throw new NotSupportedException();
        public Task<long> RemoveByPatternAsync(string pattern) => throw new NotSupportedException();
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan? expiration = null) => throw new NotSupportedException();
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
}
