using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace QFace.Sdk.RedisCache.Services;

/// <summary>
/// Main implementation of Redis cache service
/// </summary>
public class RedisCacheService : IRedisCacheService
{
    private readonly Providers.IRedisProvider _provider;
    private readonly Models.RedisCacheOptions _options;
    private readonly ILogger _logger;
    private readonly string _keyPrefix;
    private readonly FillFence _fillFence;

    private static readonly ConcurrentDictionary<FlightKey, Task> Flights = new();

    private static readonly ConditionalWeakTable<Providers.IRedisProvider, FillFence> FillFences = new();

    private readonly record struct FlightKey(KeyFills Key, Type Type, long KeyGeneration, long WildcardGeneration);

    private sealed class KeyFills
    {
        public long Generation;
        public int Readers;
        public int Invalidations;
        public HashSet<Task> PendingStores { get; } = new();
    }

    private sealed class FillFence
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, KeyFills> _keys = new(StringComparer.Ordinal);
        private long _wildcardGeneration;
        private int _wildcardInvalidations;

        public readonly record struct Fill(FillFence Fence, string Key, KeyFills State, long KeyGeneration, long WildcardGeneration) : IDisposable
        {
            public FlightKey FlightKey(Type type) => new(State, type, KeyGeneration, WildcardGeneration);

            public void Dispose()
            {
                lock (Fence._gate)
                {
                    State.Readers--;
                    Fence.Prune(Key, State);
                }
            }
        }

        public Fill Enter(string key)
        {
            lock (_gate)
            {
                var state = GetOrAdd(key);
                state.Readers++;
                return new Fill(this, key, state, state.Generation, _wildcardGeneration);
            }
        }

        public async Task StoreAsync(Fill fill, Func<Task> store)
        {
            TaskCompletionSource<bool> completion;
            var state = fill.State;
            lock (_gate)
            {
                if (state.Invalidations != 0 || _wildcardInvalidations != 0 ||
                    fill.KeyGeneration != state.Generation || fill.WildcardGeneration != _wildcardGeneration)
                {
                    return;
                }

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.PendingStores.Add(completion.Task);
            }

            try
            {
                await store();
            }
            finally
            {
                lock (_gate)
                {
                    state.PendingStores.Remove(completion.Task);
                }

                completion.SetResult(true);
            }
        }

        public async Task<TResult> InvalidateAsync<TResult>(Func<Task<TResult>> invalidate, string[]? keys = null)
        {
            Task[] pending;
            var affected = new List<KeyValuePair<string, KeyFills>>();
            lock (_gate)
            {
                if (keys is null)
                {
                    _wildcardGeneration++;
                    _wildcardInvalidations++;
                    pending = _keys.Values.SelectMany(state => state.PendingStores).ToArray();
                }
                else
                {
                    foreach (var key in keys.Distinct(StringComparer.Ordinal))
                    {
                        var state = GetOrAdd(key);
                        state.Generation++;
                        state.Invalidations++;
                        affected.Add(new(key, state));
                    }

                    pending = affected.SelectMany(entry => entry.Value.PendingStores).ToArray();
                }
            }

            try
            {
                // A fill already writing must finish before deletion. New stores cannot enter the fence.
                await Task.WhenAll(pending);
                return await invalidate();
            }
            finally
            {
                lock (_gate)
                {
                    if (keys is null)
                    {
                        _wildcardGeneration++;
                        _wildcardInvalidations--;
                    }
                    else
                    {
                        foreach (var (key, state) in affected)
                        {
                            state.Generation++;
                            state.Invalidations--;
                            Prune(key, state);
                        }
                    }
                }
            }
        }

        private KeyFills GetOrAdd(string key)
        {
            if (!_keys.TryGetValue(key, out var state))
            {
                state = new KeyFills();
                _keys.Add(key, state);
            }

            return state;
        }

        private void Prune(string key, KeyFills state)
        {
            if (state.Readers == 0 && state.Invalidations == 0)
            {
                _keys.Remove(key);
            }
        }
    }

    private static readonly TimeSpan FlightWaitTimeout = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(bool Available, string? Payload);

    private static readonly char[] GlobChars = { '*', '?', '[', '\\' };

    public RedisCacheService(
        Providers.IRedisProvider provider,
        IOptions<Models.RedisCacheOptions> options,
        ILogger<RedisCacheService> logger)
        : this(provider, options, logger, keyPrefix: null)
    {
    }

    protected RedisCacheService(
        Providers.IRedisProvider provider,
        IOptions<Models.RedisCacheOptions> options,
        ILogger logger,
        string? keyPrefix)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = keyPrefix ?? _options.KeyPrefix;
        _fillFence = FillFences.GetValue(provider, static _ => new FillFence());
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.GetAsync<T>(fullKey);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        var fullKey = GetFullKey(key);
        var expiry = expiration ?? _options.DefaultExpiration;
        await _provider.SetAsync(fullKey, value, expiry);
    }

    public async Task<bool> RemoveAsync(string key)
    {
        var fullKey = GetFullKey(key);
        return await _fillFence.InvalidateAsync(() => _provider.RemoveAsync(fullKey), new[] { fullKey });
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.ExistsAsync(fullKey);
    }

    public async Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys)
    {
        var fullKeys = keys.Select(GetFullKey).ToArray();
        return await _provider.GetManyAsync<T>(fullKeys);
    }

    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null)
    {
        var fullKeyItems = items.ToDictionary(
            kvp => GetFullKey(kvp.Key),
            kvp => kvp.Value
        );
        var expiry = expiration ?? _options.DefaultExpiration;
        await _provider.SetManyAsync(fullKeyItems, expiry);
    }

    public async Task RemoveManyAsync(params string[] keys)
    {
        var fullKeys = keys.Select(GetFullKey).ToArray();
        await _fillFence.InvalidateAsync(async () =>
        {
            await _provider.RemoveManyAsync(fullKeys);
            return true;
        }, fullKeys);
    }

    public async Task<long> RemoveByPatternAsync(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return 0;
        }

        var fullPattern = GetFullKey(pattern);
        if (fullPattern.IndexOfAny(GlobChars) < 0)
        {
            return await _fillFence.InvalidateAsync(() => _provider.RemoveAsync(fullPattern), new[] { fullPattern }) ? 1 : 0;
        }

        return await _fillFence.InvalidateAsync(() => _provider.RemoveByPatternAsync(fullPattern));
    }

    public async Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        var fullKey = GetFullKey(key);
        var expiry = expiration ?? _options.DefaultExpiration;
        return await _provider.SetIfNotExistsAsync(fullKey, value, expiry);
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null)
    {
        var fullKey = GetFullKey(key);
        var readFailed = false;

        try
        {
            var (found, cached) = await TryReadAsync<T>(fullKey);
            if (found)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            readFailed = true;
            _logger.LogWarning(ex, "Cache read failed; loading from origin");
        }

        using var fill = _fillFence.Enter(fullKey);
        var flightKey = fill.FlightKey(typeof(T));
        var tcs = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Flights.GetOrAdd(flightKey, tcs.Task);

        if (!ReferenceEquals(existing, tcs.Task))
        {
            try
            {
                var shared = await ((Task<Snapshot>)existing).WaitAsync(FlightWaitTimeout);
                if (shared.Available)
                {
                    return FromSnapshot<T>(shared);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Shared cache load unavailable ({ExceptionType}); loading from origin independently", ex.GetType().Name);
            }

            var independent = await factory();
            if (!readFailed)
            {
                await TryStoreAsync(fullKey, independent, expiration, fill);
            }

            return independent;
        }

        T value;
        try
        {
            value = await factory();
        }
        catch (Exception ex)
        {
            Flights.TryRemove(new KeyValuePair<FlightKey, Task>(flightKey, tcs.Task));
            tcs.TrySetException(ex);
            _ = tcs.Task.Exception;
            throw;
        }

        try
        {
            tcs.TrySetResult(CreateSnapshot(value));
            if (!readFailed)
            {
                await TryStoreAsync(fullKey, value, expiration, fill);
            }

            return value;
        }
        finally
        {
            Flights.TryRemove(new KeyValuePair<FlightKey, Task>(flightKey, tcs.Task));
        }
    }

    private async Task<(bool Found, T Value)> TryReadAsync<T>(string fullKey)
    {
        if (_provider is Providers.IRedisValueProbe probe)
        {
            return await probe.TryGetAsync<T>(fullKey);
        }

        var cached = await _provider.GetAsync<T>(fullKey);
        if (!EqualityComparer<T>.Default.Equals(cached, default!))
        {
            return (true, cached);
        }

        return (await _provider.ExistsAsync(fullKey), cached);
    }

    private async Task TryStoreAsync<T>(string fullKey, T value, TimeSpan? expiration, FillFence.Fill fill)
    {
        if (EqualityComparer<T>.Default.Equals(value, default!))
        {
            return;
        }

        try
        {
            await _fillFence.StoreAsync(fill, () => _provider.SetAsync(fullKey, value, expiration ?? _options.DefaultExpiration));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache write failed after origin load");
        }
    }

    private Snapshot CreateSnapshot<T>(T value)
    {
        if (EqualityComparer<T>.Default.Equals(value, default!))
        {
            return new Snapshot(true, null);
        }

        if (value is string str)
        {
            return new Snapshot(true, str);
        }

        try
        {
            return new Snapshot(true, JsonConvert.SerializeObject(value, Providers.RedisJsonSettings.Settings));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cache snapshot failed ({ExceptionType}); waiters will load independently", ex.GetType().Name);
            return new Snapshot(false, null);
        }
    }

    private static T FromSnapshot<T>(Snapshot snapshot)
    {
        if (snapshot.Payload is null)
        {
            return default!;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)snapshot.Payload;
        }

        return JsonConvert.DeserializeObject<T>(snapshot.Payload, Providers.RedisJsonSettings.Settings)!;
    }

    public async Task<TimeSpan?> GetTimeToLiveAsync(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.GetTimeToLiveAsync(fullKey);
    }

    public async Task<bool> ExtendExpirationAsync(string key, TimeSpan expiration)
    {
        var fullKey = GetFullKey(key);
        return await _provider.ExtendExpirationAsync(fullKey, expiration);
    }

    public async Task<T> HashGetAsync<T>(string key, string field)
    {
        var fullKey = GetFullKey(key);
        return await _provider.HashGetAsync<T>(fullKey, field);
    }

    public async Task<bool> HashSetAsync<T>(string key, string field, T value)
    {
        var fullKey = GetFullKey(key);
        return await _provider.HashSetAsync(fullKey, field, value);
    }

    public async Task<Dictionary<string, T>> HashGetAllAsync<T>(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.HashGetAllAsync<T>(fullKey);
    }

    public async Task<long> ListPushAsync<T>(string key, T value)
    {
        var fullKey = GetFullKey(key);
        return await _provider.ListPushAsync(fullKey, value);
    }

    public async Task<T> ListPopAsync<T>(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.ListPopAsync<T>(fullKey);
    }

    public async Task<List<T>> ListGetRangeAsync<T>(string key, long start, long stop)
    {
        var fullKey = GetFullKey(key);
        return await _provider.ListGetRangeAsync<T>(fullKey, start, stop);
    }

    public async Task<bool> SetAddAsync<T>(string key, T value)
    {
        var fullKey = GetFullKey(key);
        return await _provider.SetAddAsync(fullKey, value);
    }

    public async Task<bool> SetContainsAsync<T>(string key, T value)
    {
        var fullKey = GetFullKey(key);
        return await _provider.SetContainsAsync(fullKey, value);
    }

    public async Task<List<T>> SetGetAllAsync<T>(string key)
    {
        var fullKey = GetFullKey(key);
        return await _provider.SetGetAllAsync<T>(fullKey);
    }

    private string GetFullKey(string key)
    {
        if (string.IsNullOrEmpty(_keyPrefix))
            return key;

        return $"{_keyPrefix}{key}";
    }
}
