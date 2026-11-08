namespace QFace.Sdk.RedisCache.Services;

public sealed class GlobalRedisCacheService : RedisCacheService, IGlobalRedisCacheService
{
    public GlobalRedisCacheService(
        Providers.IRedisProvider provider,
        IOptions<Models.RedisCacheOptions> options,
        ILogger<GlobalRedisCacheService> logger)
        : base(provider, options, logger, keyPrefix: string.Empty)
    {
    }
}
