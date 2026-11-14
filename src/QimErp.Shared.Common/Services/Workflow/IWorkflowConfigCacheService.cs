namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Read-only cache service for workflow configurations.
/// Used by interceptors to check workflow rules without direct database access.
/// All configuration data is read from Redis distributed cache.
/// </summary>
public interface IWorkflowConfigCacheService
{
    Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null);

    Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null);

    Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null);

    Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
        string module,
        string entityType,
        string operation,
        string? tenantId = null);

    /// <remarks>The default implementation cannot report <see cref="WorkflowConfigLookupStatus.Unknown"/>; override it.</remarks>
    async Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null)
    {
        var config = await GetEntityConfigAsync(module, entityType, tenantId);
        return config is null ? WorkflowConfigLookup.Absent : WorkflowConfigLookup.Found(config);
    }
}
