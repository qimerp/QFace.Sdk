namespace QFace.Sdk.RedisCache.Services;

/// <summary>
/// Redis cache that uses keys verbatim and never applies <see cref="Models.RedisCacheOptions.KeyPrefix"/>.
/// Use only for keys written by one service and read by another.
/// </summary>
public interface IGlobalRedisCacheService : IRedisCacheService
{
}
