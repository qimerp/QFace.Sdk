using System.Collections.Concurrent;

namespace QFace.Sdk.RedisCache.Services;

/// <summary>
/// Main implementation of Redis cache service
/// </summary>
public class RedisCacheService : IRedisCacheService
{
    private readonly Providers.IRedisProvider _provider;
    private readonly Models.RedisCacheOptions _options;
    private readonly ILogger _logger;

    private static readonly ConcurrentDictionary<FlightKey, Task> Flights = new();

    private readonly record struct FlightKey(Providers.IRedisProvider Provider, string Key, Type Type);

    private static readonly TimeSpan FlightWaitTimeout = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(bool Available, string? Payload);

    private static readonly char[] GlobChars = { '*', '?', '[', '\\' };

    public RedisCacheService(
        Providers.IRedisProvider provider,
        IOptions<Models.RedisCacheOptions> options,
        ILogger<RedisCacheService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        return await _provider.RemoveAsync(fullKey);
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
        await _provider.RemoveManyAsync(fullKeys);
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
            return await _provider.RemoveAsync(fullPattern) ? 1 : 0;
        }

        return await _provider.RemoveByPatternAsync(fullPattern);
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

        var flightKey = new FlightKey(_provider, fullKey, typeof(T));
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
                await TryStoreAsync(fullKey, independent, expiration);
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
                await TryStoreAsync(fullKey, value, expiration);
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

    private async Task TryStoreAsync<T>(string fullKey, T value, TimeSpan? expiration)
    {
        if (EqualityComparer<T>.Default.Equals(value, default!))
        {
            return;
        }

        try
        {
            await _provider.SetAsync(fullKey, value, expiration ?? _options.DefaultExpiration);
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
        if (string.IsNullOrEmpty(_options.KeyPrefix))
            return key;

        return $"{_options.KeyPrefix}{key}";
    }
}

