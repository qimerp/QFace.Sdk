using System.Collections.Concurrent;
using QimErp.Shared.Common.Services.Cache;

namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Read-only workflow definition provider. A cache miss proves nothing is published only when the tenant warm marker is present.
/// </summary>
public class WorkflowDefinitionProvider(
    ISharedDistributedCacheService cache,
    ILogger<WorkflowDefinitionProvider> logger,
    TimeProvider? timeProvider = null) : IWorkflowDefinitionProvider
{
    public static readonly TimeSpan AbsentResultLifetime = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (WorkflowDefinitionLookup Lookup, DateTimeOffset ReadAt)> _scopeCache = new();

    public async Task<PublishedWorkflowDefinition?> GetPublishedDefinitionAsync(
        string tenantId,
        string workflowCode,
        string entityType,
        CancellationToken cancellationToken = default) =>
        (await LookupPublishedDefinitionAsync(tenantId, workflowCode, entityType, cancellationToken)).Definition;

    public async Task<PublishedWorkflowDefinition?> GetPublishedDefinitionByEntityTypeAsync(
        string tenantId,
        string entityType,
        CancellationToken cancellationToken = default) =>
        (await LookupPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken)).Definition;

    public Task<WorkflowDefinitionLookup> LookupPublishedDefinitionAsync(
        string tenantId,
        string workflowCode,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogWarning("Cannot load workflow definition — TenantId is empty for WorkflowCode={WorkflowCode}",
                workflowCode);
            return Task.FromResult(WorkflowDefinitionLookup.Absent);
        }

        return ReadAsync(
            WorkflowDefinitionCacheKeys.PublishedDefinition(tenantId, workflowCode, entityType),
            tenantId,
            $"code={workflowCode}, entity={entityType}");
    }

    public Task<WorkflowDefinitionLookup> LookupPublishedDefinitionByEntityTypeAsync(
        string tenantId,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogWarning("Cannot load workflow definition — TenantId is empty for EntityType={EntityType}",
                entityType);
            return Task.FromResult(WorkflowDefinitionLookup.Absent);
        }

        return ReadAsync(
            WorkflowDefinitionCacheKeys.PublishedDefinitionByEntityType(tenantId, entityType),
            tenantId,
            $"entity={entityType}");
    }

    private async Task<WorkflowDefinitionLookup> ReadAsync(string cacheKey, string tenantId, string target)
    {
        if (_scopeCache.TryGetValue(cacheKey, out var cached)
            && (cached.Lookup.Status == WorkflowDefinitionLookupStatus.Found
                || _clock.GetUtcNow() - cached.ReadAt <= AbsentResultLifetime))
        {
            return cached.Lookup;
        }

        try
        {
            var read = await cache.ReadEntryAsync<PublishedWorkflowDefinition>(cacheKey);
            switch (read.Status)
            {
                case CacheReadStatus.Hit when read.Value is not null:
                    logger.LogDebug("Cache hit for workflow definition tenant={TenantId}, {Target}", tenantId, target);
                    return Remember(cacheKey, WorkflowDefinitionLookup.Found(read.Value));
                case CacheReadStatus.Miss:
                    logger.LogDebug("No published workflow definition in cache for tenant={TenantId}, {Target}", tenantId, target);
                    return Remember(cacheKey, WorkflowDefinitionLookup.Absent);
                default:
                    logger.LogWarning(
                        "Workflow definition could not be read from cache for tenant={TenantId}, {Target}", tenantId, target);
                    return WorkflowDefinitionLookup.Unknown;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading workflow definition from cache for tenant={TenantId}, {Target}", tenantId, target);
            return WorkflowDefinitionLookup.Unknown;
        }
    }

    private WorkflowDefinitionLookup Remember(string cacheKey, WorkflowDefinitionLookup lookup)
    {
        _scopeCache[cacheKey] = (lookup, _clock.GetUtcNow());
        return lookup;
    }
}
