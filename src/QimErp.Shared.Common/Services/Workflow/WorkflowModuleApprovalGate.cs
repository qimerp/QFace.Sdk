using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.TenantSetup;

namespace QimErp.Shared.Common.Services.Workflow;

public sealed class WorkflowModuleApprovalGate(
    ITenantModuleAccessService moduleAccess,
    IWorkflowConfigCacheService configCache,
    ILogger<WorkflowModuleApprovalGate> logger) : IWorkflowModuleApprovalGate
{
    public async Task<bool> IsApprovalModuleEnabledAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogDebug(
                "[WorkflowModuleApprovalGate] Empty tenant — approval workflows disabled");
            return false;
        }

        var access = await moduleAccess.GetModuleAccessAsync(
            tenantId, ModuleKeys.Workflow, cancellationToken);

        switch (access)
        {
            case ModuleAccess.Enabled:
                return true;
            case ModuleAccess.NotInstalled:
                logger.LogDebug(
                    "[WorkflowModuleApprovalGate] Module {ModuleKey} not installed for tenant {TenantId} — skipping approval workflow",
                    ModuleKeys.Workflow, tenantId);
                return false;
            default:
                logger.LogWarning(
                    "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId} — treating {ModuleKey} as installed",
                    tenantId, ModuleKeys.Workflow);
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                    WorkflowApprovalGateMetrics.AssumedEnabled, null, null);
                return true;
        }
    }

    public Task<WorkflowApprovalGateState> GetApprovalStateAsync(
        string? tenantId,
        string? module,
        string entityType,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(moduleAccess, configCache, logger, tenantId, module, entityType, cancellationToken);

    internal static async Task<WorkflowApprovalGateState> EvaluateAsync(
        ITenantModuleAccessService moduleAccess,
        IWorkflowConfigCacheService configCache,
        ILogger logger,
        string? tenantId,
        string? module,
        string entityType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogDebug(
                "[WorkflowModuleApprovalGate] Empty tenant — approval workflows disabled for {EntityType}",
                entityType);
            return WorkflowApprovalGateState.Off;
        }

        var access = await moduleAccess.GetModuleAccessAsync(
            tenantId, ModuleKeys.Workflow, cancellationToken);

        if (access == ModuleAccess.Enabled)
            return WorkflowApprovalGateState.On;

        if (access == ModuleAccess.NotInstalled)
        {
            logger.LogDebug(
                "[WorkflowModuleApprovalGate] Module {ModuleKey} not installed for tenant {TenantId} — skipping approval workflow for {EntityType}",
                ModuleKeys.Workflow, tenantId, entityType);
            return WorkflowApprovalGateState.Off;
        }

        if (string.IsNullOrWhiteSpace(module))
        {
            logger.LogWarning(
                "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId} and no workflow module is configured for {EntityType} — refusing the write",
                tenantId, entityType);
            WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                WorkflowApprovalGateMetrics.ModuleUnresolved, module, entityType);
            return WorkflowApprovalGateState.Unavailable;
        }

        var lookup = await configCache.LookupEntityConfigAsync(module, entityType, tenantId);
        switch (lookup.Status)
        {
            case WorkflowConfigLookupStatus.Found:
                logger.LogWarning(
                    "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId}; workflow configuration found for {Module}/{EntityType} — applying it",
                    tenantId, module, entityType);
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                    WorkflowApprovalGateMetrics.ConfigFound, module, entityType);
                return WorkflowApprovalGateState.On;
            case WorkflowConfigLookupStatus.Absent:
                logger.LogWarning(
                    "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId}; no workflow configuration for {Module}/{EntityType} — saving without approval",
                    tenantId, module, entityType);
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                    WorkflowApprovalGateMetrics.ConfigAbsent, module, entityType);
                return WorkflowApprovalGateState.Off;
            default:
                logger.LogWarning(
                    "[WorkflowModuleApprovalGate] Module state and workflow configuration both unreadable for tenant {TenantId}, {Module}/{EntityType} — refusing the write",
                    tenantId, module, entityType);
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                    WorkflowApprovalGateMetrics.ConfigUnknown, module, entityType);
                return WorkflowApprovalGateState.Unavailable;
        }
    }
}
