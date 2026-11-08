using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QFace.Sdk.RedisCache.Models;
using QFace.Sdk.RedisCache.Services;
using QFace.Sdk.RedisCache.Services.Providers;
using Xunit;

namespace QFace.Sdk.RedisCache.Tests;

public class CacheInvalidationTests
{
    [Theory]
    [InlineData("exact-pattern")]
    [InlineData("wildcard")]
    [InlineData("single")]
    [InlineData("many")]
    public async Task AfterEvictionCallerMustNotJoinPreMutationFill(string operation)
    {
        var store = new Store();
        var read = Cache(store);
        var write = Cache(store);
        var originStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeOrigin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = read.GetOrSetAsync("permissions", async () =>
        {
            originStarted.SetResult();
            await resumeOrigin.Task;
            return "old";
        });
        await originStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await (operation switch
        {
            "exact-pattern" => write.RemoveByPatternAsync("permissions"),
            "wildcard" => write.RemoveByPatternAsync("permis*"),
            "single" => write.RemoveAsync("permissions"),
            _ => write.RemoveManyAsync("permissions")
        });
        var second = write.GetOrSetAsync("permissions", () => Task.FromResult("new"));
        resumeOrigin.SetResult();
        Assert.Equal("old", await first);
        Assert.Equal("new", await second);
        Assert.Equal("new", await read.GetAsync<string>("permissions"));
    }

    [Fact]
    public async Task EvictionDrainsAnAlreadyAdmittedStoreBeforeDeleting()
    {
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store
        {
            BeforeSet = async () =>
            {
                storeStarted.SetResult();
                await resumeStore.Task;
            }
        };
        var read = Cache(store);
        var write = Cache(store);
        var first = read.GetOrSetAsync("permissions", () => Task.FromResult("old"));
        await storeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var eviction = write.RemoveAsync("permissions");
        try
        {
            Assert.False(eviction.IsCompleted);
        }
        finally
        {
            resumeStore.SetResult();
        }

        Assert.Equal("old", await first);
        Assert.True(await eviction);
        Assert.Null(await read.GetAsync<string>("permissions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactEvictionDoesNotWaitForAnUnrelatedStore(bool many)
    {
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store
        {
            BeforeSet = async () =>
            {
                storeStarted.SetResult();
                await resumeStore.Task;
            }
        };
        var read = Cache(store);
        var write = Cache(store);
        var unrelated = read.GetOrSetAsync("unrelated", () => Task.FromResult("fresh"));
        await storeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Task eviction = many ? write.RemoveManyAsync("permissions", "other") : write.RemoveAsync("permissions");
            await eviction.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            resumeStore.SetResult();
        }

        Assert.Equal("fresh", await unrelated);
        Assert.Equal("fresh", await read.GetAsync<string>("unrelated"));
    }

    [Fact]
    public async Task ExactEvictionDoesNotSuppressUnrelatedStores()
    {
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store
        {
            BeforeRemove = async () =>
            {
                deleteStarted.SetResult();
                await resumeDelete.Task;
            }
        };
        var read = Cache(store);
        var write = Cache(store);
        var eviction = write.RemoveAsync("permissions");
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.Equal("fresh", await read.GetOrSetAsync("unrelated", () => Task.FromResult("fresh")));
            Assert.Equal("fresh", await read.GetAsync<string>("unrelated"));
        }
        finally
        {
            resumeDelete.SetResult();
        }

        await eviction;
    }

    [Fact]
    public async Task SuccessfulFactoryIsRunOnceWhenSetFails()
    {
        var read = Cache(new Store { FailSet = true });
        var reads = 0;
        var result = await read.GetOrSetAsync("key", () => Task.FromResult(++reads));
        Assert.Equal(1, result);
        Assert.Equal(1, reads);
    }

    private static RedisCacheService Cache(Store provider) =>
        new(provider, Options.Create(new RedisCacheOptions()), NullLogger<RedisCacheService>.Instance);

    private sealed class Store : IRedisProvider, IRedisValueProbe
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _values = new();
        public bool FailSet { get; init; }
        public Func<Task>? BeforeSet { get; init; }
        public Func<Task>? BeforeRemove { get; init; }
        public Task<(bool Found, T Value)> TryGetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var val) ? (true, (T)val) : (false, default(T)!));
        public async Task<T> GetAsync<T>(string key) => (await TryGetAsync<T>(key)).Value;
        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            if (FailSet)
                throw new InvalidOperationException();
            if (BeforeSet is not null)
                await BeforeSet();

            _values[key] = value!;
        }
        public async Task<bool> RemoveAsync(string key)
        {
            if (BeforeRemove is not null)
                await BeforeRemove();

            return _values.TryRemove(key, out _);
        }
        public Task<bool> ExistsAsync(string key) => Task.FromResult(_values.ContainsKey(key));
        public Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys) => throw new NotSupportedException();
        public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null) => throw new NotSupportedException();
        public async Task RemoveManyAsync(params string[] keys)
        {
            foreach (var key in keys)
                await RemoveAsync(key);
        }
        public async Task<long> RemoveByPatternAsync(string pattern)
        {
            Assert.EndsWith("*", pattern);
            var keys = _values.Keys.Where(key => key.StartsWith(pattern[..^1], StringComparison.Ordinal)).ToArray();
            await RemoveManyAsync(keys);
            return keys.LongLength;
        }
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
