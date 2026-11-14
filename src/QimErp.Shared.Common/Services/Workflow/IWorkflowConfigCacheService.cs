namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Read-only cache service for workflow configurations.
/// Used by interceptors to check workflow rules without direct database access.
/// All configuration data is read from Redis distributed cache.
/// </summary>
public interface IWorkflowConfigCacheService
{
    [Obsolete(WorkflowConfigCacheObsolete.Message)]
    Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null);

    [Obsolete(WorkflowConfigCacheObsolete.Message)]
    Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null);

    [Obsolete(WorkflowConfigCacheObsolete.Message)]
    Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null);

    [Obsolete(WorkflowConfigCacheObsolete.Message)]
    Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
        string module,
        string entityType,
        string operation,
        string? tenantId = null);

    /// <remarks>The default implementation cannot report <see cref="WorkflowConfigLookupStatus.Unknown"/>; override it.</remarks>
    async Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null)
    {
#pragma warning disable CS0618
        var config = await GetEntityConfigAsync(module, entityType, tenantId);
#pragma warning restore CS0618
        return config is null ? WorkflowConfigLookup.Absent : WorkflowConfigLookup.Found(config);
    }

    /// <remarks>
    /// Without the marker a missing configuration key is not proof that nothing is bound. The default
    /// implementation reports <see cref="WorkflowCacheWarmStatus.Unknown"/>, which refuses such writes; override it.
    /// </remarks>
    Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string? tenantId = null) =>
        Task.FromResult(WorkflowCacheWarmStatus.Unknown);
}

public static class WorkflowConfigCacheObsolete
{
    public const string Message =
        "Not safe for approval decisions: a read failure reads as 'no approval'. " +
        "Use IWorkflowModuleApprovalGate.GetApprovalStateAsync / LookupEntityConfigAsync.";
}
