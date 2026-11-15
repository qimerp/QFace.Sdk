namespace QimErp.Shared.Common.Services.Cache;

public enum CacheReadStatus
{
    Unavailable = 0,
    Hit = 1,
    Miss = 2,
}

public readonly record struct CacheReadResult<T>(CacheReadStatus Status, T? Value)
{
    public static CacheReadResult<T> Found(T value) => new(CacheReadStatus.Hit, value);

    public static CacheReadResult<T> Missing() => new(CacheReadStatus.Miss, default);

    public static CacheReadResult<T> Unreadable() => new(CacheReadStatus.Unavailable, default);
}

/// <summary>
/// Implemented by caches that can tell a missing key from a failed or undecodable read.
/// </summary>
public interface ICacheEntryReader
{
    Task<CacheReadResult<T>> ReadEntryAsync<T>(string key, string? region = null);
}

public static class CacheEntryReadExtensions
{
    /// <remarks>Caches without <see cref="ICacheEntryReader"/> report null as <see cref="CacheReadStatus.Miss"/> and let exceptions propagate.</remarks>
    public static async Task<CacheReadResult<T>> ReadEntryAsync<T>(
        this IDistributedCacheService cache,
        string key,
        string? region = null)
    {
        if (cache is ICacheEntryReader reader)
            return await reader.ReadEntryAsync<T>(key, region);

        var value = await cache.GetAsync<T>(key, region);
        return value is null ? CacheReadResult<T>.Missing() : CacheReadResult<T>.Found(value);
    }
}
