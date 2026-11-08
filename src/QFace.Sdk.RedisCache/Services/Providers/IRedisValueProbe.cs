namespace QFace.Sdk.RedisCache.Services.Providers;

internal interface IRedisValueProbe
{
    Task<(bool Found, T Value)> TryGetAsync<T>(string key);
}
