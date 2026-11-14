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
    private readonly ConcurrentDictionary<string, EntityWorkflowConfig> _localCache = new();
    private static readonly TimeSpan WarmMarkerReuseWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmMarkerClockSkew = TimeSpan.FromMinutes(5);
    private static int _invalidMaxAgeLogged;

    private readonly ConcurrentDictionary<string, (WorkflowTenantWarmMarker Marker, DateTimeOffset ReadAt)> _warmMarkers = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _warmMarkerMaxAge;

    public WorkflowConfigCacheService(
        ISharedDistributedCacheService cache,
        ITenantContext tenantContext,
        ILogger<WorkflowConfigCacheService> logger)
        : this(cache, tenantContext, logger, null, null)
    {
    }

    public WorkflowConfigCacheService(
        ISharedDistributedCacheService cache,
        ITenantContext tenantContext,
        ILogger<WorkflowConfigCacheService> logger,
        IConfiguration? configuration,
        TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _tenantContext = tenantContext;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _warmMarkerMaxAge = ResolveWarmMarkerMaxAge(configuration?[WorkflowTenantWarmMarker.MaxAgeSettingKey]);
    }

    private TimeSpan ResolveWarmMarkerMaxAge(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return WorkflowTenantWarmMarker.DefaultMaxAge;

        if (configured.Contains(':')
            && TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var maxAge)
            && maxAge > TimeSpan.Zero
            && maxAge <= WorkflowTenantWarmMarker.DefaultMaxAge)
        {
            return maxAge;
        }

        if (Interlocked.Exchange(ref _invalidMaxAgeLogged, 1) == 0)
        {
            _logger.LogError(
                "{Setting}='{Value}' is not a positive hh:mm:ss duration of at most {Cap}; using {Cap}",
                WorkflowTenantWarmMarker.MaxAgeSettingKey, configured, WorkflowTenantWarmMarker.DefaultMaxAge,
                WorkflowTenantWarmMarker.DefaultMaxAge);
        }

        return WorkflowTenantWarmMarker.DefaultMaxAge;
    }

    public async Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        (await LookupEntityConfigAsync(module, entityType, tenantId)).Config;

    public Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        LookupEntityConfigInternalAsync(ResolveTenantId(tenantId), module, entityType);

    public async Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string module, string entityType, string? tenantId = null)
    {
        var resolvedTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(resolvedTenantId))
            return WorkflowCacheWarmStatus.NotWarm;

        var cacheKey = WorkflowDefinitionCacheKeys.TenantWarmMarker(resolvedTenantId);
        var now = _timeProvider.GetUtcNow();
        WorkflowTenantWarmMarker marker;
        if (_warmMarkers.TryGetValue(cacheKey, out var cached) && now - cached.ReadAt <= WarmMarkerReuseWindow)
        {
            marker = cached.Marker;
        }
        else
        {
            try
            {
                var read = await _cache.ReadEntryAsync<WorkflowTenantWarmMarker>(cacheKey);
                switch (read.Status)
                {
                    case CacheReadStatus.Hit when read.Value is not null:
                        marker = read.Value;
                        break;
                    case CacheReadStatus.Miss:
                        _warmMarkers.TryRemove(cacheKey, out _);
                        return WorkflowCacheWarmStatus.NotWarm;
                    default:
                        _warmMarkers.TryRemove(cacheKey, out _);
                        _logger.LogWarning("Workflow warm marker could not be read for tenant={TenantId}", resolvedTenantId);
                        return WorkflowCacheWarmStatus.Unknown;
                }
            }
            catch (Exception ex)
            {
                _warmMarkers.TryRemove(cacheKey, out _);
                _logger.LogError(ex, "Error reading workflow warm marker for tenant={TenantId}", resolvedTenantId);
                return WorkflowCacheWarmStatus.Unknown;
            }
        }

        if (!marker.IsBoundSetPresent)
        {
            _warmMarkers.TryRemove(cacheKey, out _);
            _logger.LogWarning(
                "Workflow warm marker for tenant={TenantId} is version {Version} without a Bound set; treating the cache as not warm",
                resolvedTenantId, marker.Version);
            return WorkflowCacheWarmStatus.NotWarm;
        }

        var age = now - marker.WarmedAt;
        if (age > _warmMarkerMaxAge || -age > WarmMarkerClockSkew)
        {
            _warmMarkers.TryRemove(cacheKey, out _);
            _logger.LogWarning(
                "Workflow warm marker for tenant={TenantId} has warmedAt {WarmedAt}, outside the accepted window (max age {MaxAge}); treating the cache as not warm",
                resolvedTenantId, marker.WarmedAt, _warmMarkerMaxAge);
            return WorkflowCacheWarmStatus.NotWarm;
        }

        if (!_warmMarkers.TryGetValue(cacheKey, out var current) || !ReferenceEquals(current.Marker, marker))
            _warmMarkers[cacheKey] = (marker, now);

        return marker.IsBound(module, entityType)
            ? WorkflowCacheWarmStatus.BoundAtWarm
            : WorkflowCacheWarmStatus.Warm;
    }

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
            return WorkflowConfigLookup.Found(cached);
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
