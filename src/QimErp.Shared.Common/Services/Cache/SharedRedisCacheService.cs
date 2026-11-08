using QFace.Sdk.RedisCache.Services;

namespace QimErp.Shared.Common.Services.Cache;

public sealed class SharedRedisCacheService(
    IGlobalRedisCacheService redisCacheService,
    ILogger<RedisCacheService> logger,
    IConfiguration configuration)
    : RedisCacheService(redisCacheService, logger, configuration), ISharedDistributedCacheService;
