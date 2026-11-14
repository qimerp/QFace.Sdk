using Microsoft.Extensions.Hosting;

namespace QimErp.Shared.Common.Services.Cache;

internal sealed class RedisCacheDisabledStartupCheck(ILogger<RedisCacheDisabledStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogError(
            "RedisCache:Enabled is false. Shared cache reads report every key as missing, including the workflow warm marker, " +
            "so approval gating refuses saves of workflow-enabled records (503) unless their tenant module snapshot says " +
            "Workflow is not installed. Enable the cache in any environment that saves workflow-enabled records.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
