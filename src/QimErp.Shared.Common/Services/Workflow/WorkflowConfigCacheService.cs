using QimErp.Shared.Common.Services.MultiTenancy;
using System.Collections.Concurrent;
using QimErp.Shared.Common.Services.Cache;

namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Read-only cache service for workflow configurations.
/// Retrieves configuration data from Redis distributed cache only.
/// Uses an in-process L1 dictionary keyed by tenant+module+entity.
/// </summary>
public class WorkflowConfigCacheService : IWorkflowConfigCacheService
{
    private readonly ISharedDistributedCacheService _cache;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<WorkflowConfigCacheService> _logger;
    private readonly ConcurrentDictionary<string, EntityWorkflowConfig?> _localCache = new();

    public WorkflowConfigCacheService(
        ISharedDistributedCacheService cache,
        ITenantContext tenantContext,
        ILogger<WorkflowConfigCacheService> logger)
    {
        _cache = cache;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        (await LookupEntityConfigAsync(module, entityType, tenantId)).Config;

    public Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        LookupEntityConfigInternalAsync(ResolveTenantId(tenantId), module, entityType);

    public async Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null)
    {
        var config = await GetEntityConfigAsync(module, entityType, tenantId);
        if (config == null) return false;

        return operation.ToUpper() switch
        {
            "CREATE" => config.EnableWorkflowForCreate,
            "UPDATE" => config.EnableWorkflowForUpdate,
            "DELETE" => config.EnableWorkflowForDelete,
            _ => false
        };
    }

    public async Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null)
    {
        var config = await GetEntityConfigAsync(module, entityType, tenantId);
        if (config == null) return null;

        return operation.ToUpper(CultureInfo.CurrentCulture) switch
        {
            "CREATE" => config.CreateWorkflowCode,
            _ => null
        };
    }

    public async Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
        string module,
        string entityType,
        string operation,
        string? tenantId = null)
    {
        var config = await GetEntityConfigAsync(module, entityType, tenantId);
        if (config == null) return [];

        return operation.ToUpper(CultureInfo.CurrentCulture) switch
        {
            "CREATE" => config.CreateTriggerConditions,
            _ => []
        };
    }

    private async Task<WorkflowConfigLookup> LookupEntityConfigInternalAsync(string tenantId, string module, string entityType)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning(
                "Cannot load workflow config — TenantId is empty for module={Module}, entity={EntityType}",
                module, entityType);
            return WorkflowConfigLookup.Absent;
        }

        var cacheKey = WorkflowDefinitionCacheKeys.Configuration(tenantId, module, entityType);

        if (_localCache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("L1 hit for workflow config key: {Key}", cacheKey);
            return cached is null ? WorkflowConfigLookup.Absent : WorkflowConfigLookup.Found(cached);
        }

        try
        {
            var read = await _cache.ReadEntryAsync<EntityWorkflowConfig>(cacheKey);
            switch (read.Status)
            {
                case CacheReadStatus.Hit when read.Value is not null:
                    _localCache[cacheKey] = read.Value;
                    return WorkflowConfigLookup.Found(read.Value);
                case CacheReadStatus.Miss:
                    _localCache[cacheKey] = null;
                    _logger.LogDebug(
                        "No workflow configuration found in cache for tenant={TenantId}, module={Module}, entity={EntityType}",
                        tenantId, module, entityType);
                    return WorkflowConfigLookup.Absent;
                default:
                    _logger.LogWarning(
                        "Workflow configuration could not be read from cache for tenant={TenantId}, module={Module}, entity={EntityType}",
                        tenantId, module, entityType);
                    return WorkflowConfigLookup.Unknown;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error retrieving workflow configuration from cache for tenant={TenantId}, module={Module}, entity={EntityType}",
                tenantId, module, entityType);
            return WorkflowConfigLookup.Unknown;
        }
    }

    private string ResolveTenantId(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) ? tenantId : _tenantContext.TenantId ?? string.Empty;
}
