using Microsoft.Extensions.Hosting;

namespace QimErp.Shared.Common.Services.Cache;

internal sealed class RedisCacheDisabledStartupCheck(ILogger<RedisCacheDisabledStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogError(
            "RedisCache:Enabled is false. Shared cache reads report every key as missing, so workflow approval gating " +
            "cannot see the tenant module snapshot, workflow configuration or published definitions and saves workflow-enabled " +
            "records without approval. Enable the cache in any environment that relies on approvals.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
