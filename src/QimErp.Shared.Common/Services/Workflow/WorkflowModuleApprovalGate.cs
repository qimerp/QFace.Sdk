using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.TenantSetup;

namespace QimErp.Shared.Common.Services.Workflow;

public sealed class WorkflowModuleApprovalGate(
    ITenantModuleAccessService moduleAccess,
    IWorkflowConfigCacheService configCache,
    ILogger<WorkflowModuleApprovalGate> logger,
    IWorkflowDefinitionProvider? definitions = null) : IWorkflowModuleApprovalGate
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
        EvaluateAsync(moduleAccess, configCache, definitions, logger, tenantId, module, entityType, cancellationToken);

    internal static async Task<WorkflowApprovalGateState> EvaluateAsync(
        ITenantModuleAccessService moduleAccess,
        IWorkflowConfigCacheService configCache,
        IWorkflowDefinitionProvider? definitions,
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
            logger.LogError(
                "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId} and no Workflow:Module is configured for {EntityType} — refusing the write",
                tenantId, entityType);
            WorkflowApprovalGateMetrics.RecordModuleStateUnknown(
                WorkflowApprovalGateMetrics.ModuleUnresolved, module, entityType);
            return WorkflowApprovalGateState.Unavailable;
        }

        var lookup = await configCache.LookupEntityConfigAsync(module, entityType, tenantId);
        if (lookup.Status == WorkflowConfigLookupStatus.Found)
            return Decide(WorkflowApprovalGateState.On, WorkflowApprovalGateMetrics.ConfigFound,
                "workflow configuration found — applying it");

        if (lookup.Status == WorkflowConfigLookupStatus.Unknown)
            return Decide(WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.ConfigUnknown,
                "workflow configuration unreadable — refusing the write");

        var definition = definitions is null
            ? WorkflowDefinitionLookup.Absent
            : await definitions.LookupPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken);

        return definition.Status switch
        {
            WorkflowDefinitionLookupStatus.Found => Decide(WorkflowApprovalGateState.On,
                WorkflowApprovalGateMetrics.DefinitionFound, "no configuration but a published definition — applying it"),
            WorkflowDefinitionLookupStatus.Absent => Decide(WorkflowApprovalGateState.Off,
                WorkflowApprovalGateMetrics.NothingBound, "no configuration or published definition — saving without approval"),
            _ => Decide(WorkflowApprovalGateState.Unavailable,
                WorkflowApprovalGateMetrics.DefinitionUnknown, "published definition unreadable — refusing the write")
        };

        WorkflowApprovalGateState Decide(WorkflowApprovalGateState state, string outcome, string reason)
        {
            logger.LogWarning(
                "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId}, {Module}/{EntityType}: {Reason}",
                tenantId, module, entityType, reason);
            WorkflowApprovalGateMetrics.RecordModuleStateUnknown(outcome, module, entityType);
            return state;
        }
    }

    /// <summary>
    /// Decides an operation whose workflow configuration key is absent: no active published definition means Off;
    /// an unreadable definition, or an active one for UPDATE/DELETE (whose route cannot be resolved), means Unavailable.
    /// </summary>
    internal static async Task<WorkflowApprovalGateState> ResolveWithoutConfigAsync(
        IWorkflowDefinitionProvider? definitions,
        ILogger logger,
        string tenantId,
        string? module,
        string entityType,
        string operation,
        CancellationToken cancellationToken)
    {
        if (definitions is null)
            return WorkflowApprovalGateState.Off;

        var lookup = await definitions.LookupPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken);
        if (lookup.Status == WorkflowDefinitionLookupStatus.Unknown)
        {
            logger.LogWarning(
                "[WorkflowModuleApprovalGate] No workflow configuration and the published definition is unreadable for tenant {TenantId}, {Module}/{EntityType} {Operation} — refusing the write",
                tenantId, module, entityType, operation);
            WorkflowApprovalGateMetrics.RecordStateUnreadable(
                WorkflowApprovalGateMetrics.DefinitionSource, module, entityType, operation);
            return WorkflowApprovalGateState.Unavailable;
        }

        if (lookup.Definition is not { IsActive: true })
            return WorkflowApprovalGateState.Off;

        if (operation == "CREATE")
            return WorkflowApprovalGateState.On;

        logger.LogError(
            "[WorkflowModuleApprovalGate] Workflow configuration is missing for tenant {TenantId}, {Module}/{EntityType} but an active workflow definition is published — refusing {Operation} until Platform rewrites the configuration",
            tenantId, module, entityType, operation);
        WorkflowApprovalGateMetrics.RecordStateUnreadable(
            WorkflowApprovalGateMetrics.ConfigMissingSource, module, entityType, operation);
        return WorkflowApprovalGateState.Unavailable;
    }
}
