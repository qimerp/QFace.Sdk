using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QFace.Sdk.RedisCache.Extensions;
using QFace.Sdk.RedisCache.Models;
using QFace.Sdk.RedisCache.Services;
using QFace.Sdk.RedisCache.Services.Providers;
using Xunit;

namespace QFace.Sdk.RedisCache.Tests;

public class KeyPrefixTests
{
    private const string SharedKey = "qface:qimerp:tenant-1:shared:tenant_module_snapshot";

    [Fact]
    public async Task RedisCacheService_AppliesKeyPrefix()
    {
        var provider = new RecordingRedisProvider();
        var service = new RedisCacheService(provider, Options("corehr:"), NullLogger<RedisCacheService>.Instance);

        await service.SetAsync("k", "v");
        await service.GetAsync<string>("k");
        await service.RemoveByPatternAsync("p*");

        provider.Keys.Should().Equal("corehr:k", "corehr:k", "corehr:p*");
    }

    [Fact]
    public async Task GlobalRedisCacheService_IgnoresKeyPrefix()
    {
        var provider = new RecordingRedisProvider();
        var service = new GlobalRedisCacheService(provider, Options("corehr:"), NullLogger<GlobalRedisCacheService>.Instance);

        await service.SetAsync(SharedKey, "v");
        await service.GetAsync<string>(SharedKey);
        await service.ExistsAsync(SharedKey);
        await service.GetManyAsync<string>(SharedKey);
        await service.RemoveByPatternAsync("qface:qimerp:*:shared:*");

        provider.Keys.Should().Equal(SharedKey, SharedKey, SharedKey, SharedKey, "qface:qimerp:*:shared:*");
    }

    [Fact]
    public async Task PrefixedWriterAndGlobalReader_DoNotShareKeys_WhileGlobalWriterAndReaderDo()
    {
        var provider = new RecordingRedisProvider();
        var platform = new GlobalRedisCacheService(provider, Options(""), NullLogger<GlobalRedisCacheService>.Instance);
        var coreHrShared = new GlobalRedisCacheService(provider, Options("corehr:"), NullLogger<GlobalRedisCacheService>.Instance);
        var coreHrPrivate = new RedisCacheService(provider, Options("corehr:"), NullLogger<RedisCacheService>.Instance);

        await platform.SetAsync(SharedKey, "snapshot");

        (await coreHrShared.GetAsync<string>(SharedKey)).Should().Be("snapshot");
        (await coreHrPrivate.GetAsync<string>(SharedKey)).Should().BeNull();
    }

    [Fact]
    public void AddRedisCache_RegistersPrefixedAndGlobalServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCache:KeyPrefix"] = "corehr:" })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRedisCache(configuration);
        services.AddSingleton<IRedisProvider>(new RecordingRedisProvider());

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRedisCacheService>().Should().BeOfType<RedisCacheService>();
        scope.ServiceProvider.GetRequiredService<IGlobalRedisCacheService>().Should().BeOfType<GlobalRedisCacheService>();
    }

    private static IOptions<RedisCacheOptions> Options(string prefix) =>
        Microsoft.Extensions.Options.Options.Create(new RedisCacheOptions { KeyPrefix = prefix });

    private sealed class RecordingRedisProvider : IRedisProvider
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);
        public List<string> Keys { get; } = [];

        private string Record(string key)
        {
            Keys.Add(key);
            return key;
        }

        public Task<T> GetAsync<T>(string key) =>
            Task.FromResult(_store.TryGetValue(Record(key), out var v) ? (T)v! : default!);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            _store[Record(key)] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key) => Task.FromResult(_store.Remove(Record(key)));
        public Task<bool> ExistsAsync(string key) => Task.FromResult(_store.ContainsKey(Record(key)));

        public Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys)
        {
            foreach (var key in keys) Record(key);
            return Task.FromResult(new Dictionary<string, T>());
        }

        public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null) => throw new NotSupportedException();
        public Task RemoveManyAsync(params string[] keys) => throw new NotSupportedException();
        public Task<long> RemoveByPatternAsync(string pattern) { Record(pattern); return Task.FromResult(0L); }
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
