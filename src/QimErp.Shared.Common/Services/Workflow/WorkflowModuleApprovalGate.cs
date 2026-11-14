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
        string operation,
        string? workflowCode = null,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            moduleAccess, configCache, definitions, logger, tenantId, module, entityType, workflowCode, operation, cancellationToken);

    internal static async Task<WorkflowApprovalGateState> EvaluateAsync(
        ITenantModuleAccessService moduleAccess,
        IWorkflowConfigCacheService configCache,
        IWorkflowDefinitionProvider? definitions,
        ILogger logger,
        string? tenantId,
        string? module,
        string entityType,
        string? workflowCode,
        string operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogError(
                "[WorkflowModuleApprovalGate] No tenant resolved for a workflow-enabled {EntityType} {Operation} — refusing the write",
                entityType, operation);
            WorkflowApprovalGateMetrics.RecordStateUnreadable(
                WorkflowApprovalGateMetrics.TenantMissingSource, module, entityType, operation);
            return WorkflowApprovalGateState.Unavailable;
        }

        var access = await moduleAccess.GetModuleAccessAsync(
            tenantId, ModuleKeys.Workflow, cancellationToken);

        if (access == ModuleAccess.NotInstalled)
        {
            logger.LogDebug(
                "[WorkflowModuleApprovalGate] Module {ModuleKey} not installed for tenant {TenantId} — skipping approval workflow for {EntityType}",
                ModuleKeys.Workflow, tenantId, entityType);
            return WorkflowApprovalGateState.Off;
        }

        var moduleUnknown = access != ModuleAccess.Enabled;

        if (string.IsNullOrWhiteSpace(module))
        {
            logger.LogError(
                "[WorkflowModuleApprovalGate] No Workflow:Module is configured for {EntityType} {Operation} (tenant {TenantId}, module state {ModuleAccess}) — refusing the write",
                entityType, operation, tenantId, access);
            if (moduleUnknown)
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(WorkflowApprovalGateMetrics.ModuleUnresolved, module, entityType);
            else
                WorkflowApprovalGateMetrics.RecordStateUnreadable(
                    WorkflowApprovalGateMetrics.ModuleUnresolved, module, entityType, operation);
            return WorkflowApprovalGateState.Unavailable;
        }

        var lookup = await configCache.LookupEntityConfigAsync(module, entityType, tenantId);
        if (lookup.Status == WorkflowConfigLookupStatus.Found)
        {
            Warn(WorkflowApprovalGateState.On, WorkflowApprovalGateMetrics.ConfigFound);
            return WorkflowApprovalGateState.On;
        }

        if (lookup.Status == WorkflowConfigLookupStatus.Unknown)
        {
            if (!moduleUnknown)
                WorkflowApprovalGateMetrics.RecordStateUnreadable(
                    WorkflowApprovalGateMetrics.ConfigSource, module, entityType, operation);
            Warn(WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.ConfigUnknown);
            return WorkflowApprovalGateState.Unavailable;
        }

        var (state, outcome) = await ResolveWithoutConfigAsync(
            configCache, definitions, logger, tenantId, module, entityType, operation, workflowCode, cancellationToken);
        Warn(state, outcome);
        return state;

        void Warn(WorkflowApprovalGateState decided, string decisionOutcome)
        {
            if (!moduleUnknown)
                return;

            logger.LogWarning(
                "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId}, {Module}/{EntityType} {Operation}: {Outcome} → {State}",
                tenantId, module, entityType, operation, decisionOutcome, decided);
            WorkflowApprovalGateMetrics.RecordModuleStateUnknown(decisionOutcome, module, entityType);
        }
    }

    internal static async Task<(WorkflowApprovalGateState State, string Outcome)> ResolveWithoutConfigAsync(
        IWorkflowConfigCacheService configCache,
        IWorkflowDefinitionProvider? definitions,
        ILogger logger,
        string tenantId,
        string? module,
        string entityType,
        string operation,
        string? workflowCode,
        CancellationToken cancellationToken)
    {
        var bound = await FindBoundDefinitionAsync(definitions, tenantId, entityType, workflowCode, cancellationToken);
        if (bound.Status == WorkflowDefinitionLookupStatus.Unknown)
        {
            logger.LogWarning(
                "[WorkflowModuleApprovalGate] No workflow configuration and the published definition is unreadable for tenant {TenantId}, {Module}/{EntityType} {Operation} — refusing the write",
                tenantId, module, entityType, operation);
            WorkflowApprovalGateMetrics.RecordStateUnreadable(
                WorkflowApprovalGateMetrics.DefinitionSource, module, entityType, operation);
            return (WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.DefinitionUnknown);
        }

        if (bound.Status == WorkflowDefinitionLookupStatus.Absent)
            return await ResolveNothingBoundAsync(configCache, logger, tenantId, module, entityType, operation);

        if (operation == "CREATE"
            && !string.IsNullOrWhiteSpace(workflowCode)
            && !string.Equals(bound.Definition!.WorkflowCode, workflowCode, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "[WorkflowModuleApprovalGate] {EntityType} requests workflow {WorkflowCode}, which is not published, while {ActiveWorkflowCode} is active for tenant {TenantId} — refusing the write",
                entityType, workflowCode, bound.Definition.WorkflowCode, tenantId);
            WorkflowApprovalGateMetrics.RecordStateUnreadable(
                WorkflowApprovalGateMetrics.PresetCodeUnpublished, module, entityType, operation);
            return (WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.PresetCodeUnpublished);
        }

        if (operation == "CREATE")
            return (WorkflowApprovalGateState.On, WorkflowApprovalGateMetrics.DefinitionFound);

        logger.LogError(
            "[WorkflowModuleApprovalGate] Workflow configuration is missing for tenant {TenantId}, {Module}/{EntityType} but an active workflow definition is published — refusing {Operation} until Platform rewrites the configuration",
            tenantId, module, entityType, operation);
        WorkflowApprovalGateMetrics.RecordStateUnreadable(
            WorkflowApprovalGateMetrics.ConfigMissingSource, module, entityType, operation);
        return (WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.ConfigMissingSource);
    }

    internal static async Task<WorkflowDefinitionLookup> FindBoundDefinitionAsync(
        IWorkflowDefinitionProvider? definitions,
        string tenantId,
        string entityType,
        string? workflowCode,
        CancellationToken cancellationToken)
    {
        if (definitions is null)
            return WorkflowDefinitionLookup.Absent;

        if (!string.IsNullOrWhiteSpace(workflowCode))
        {
            var byCode = await definitions.LookupPublishedDefinitionAsync(tenantId, workflowCode, entityType, cancellationToken);
            if (byCode.Status == WorkflowDefinitionLookupStatus.Unknown)
                return WorkflowDefinitionLookup.Unknown;
            if (byCode.Definition is { IsActive: true } codeDefinition)
                return WorkflowDefinitionLookup.Found(codeDefinition);
        }

        var byEntity = await definitions.LookupPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken);
        if (byEntity.Status == WorkflowDefinitionLookupStatus.Unknown)
            return WorkflowDefinitionLookup.Unknown;

        return byEntity.Definition is { IsActive: true } entityDefinition
            ? WorkflowDefinitionLookup.Found(entityDefinition)
            : WorkflowDefinitionLookup.Absent;
    }

    private static async Task<(WorkflowApprovalGateState State, string Outcome)> ResolveNothingBoundAsync(
        IWorkflowConfigCacheService configCache,
        ILogger logger,
        string tenantId,
        string? module,
        string entityType,
        string operation)
    {
        var warm = await configCache.GetTenantWarmStatusAsync(module ?? string.Empty, entityType, tenantId);
        if (warm == WorkflowCacheWarmStatus.Warm)
            return (WorkflowApprovalGateState.Off, WorkflowApprovalGateMetrics.NothingBound);

        if (warm == WorkflowCacheWarmStatus.BoundAtWarm)
        {
            logger.LogError(
                "[WorkflowModuleApprovalGate] {Module}/{EntityType} was bound when tenant {TenantId} was warmed but its configuration and definition keys are missing — refusing {Operation}",
                module, entityType, tenantId, operation);
            WorkflowApprovalGateMetrics.RecordStateUnreadable(
                WorkflowApprovalGateMetrics.BoundKeyMissing, module, entityType, operation);
            return (WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.BoundKeyMissing);
        }

        logger.LogWarning(
            "[WorkflowModuleApprovalGate] Nothing is bound for tenant {TenantId}, {Module}/{EntityType} {Operation}, but the workflow warm marker is {WarmStatus} — refusing the write",
            tenantId, module, entityType, operation, warm);
        WorkflowApprovalGateMetrics.RecordStateUnreadable(
            WorkflowApprovalGateMetrics.MarkerMissing, module, entityType, operation);
        return (WorkflowApprovalGateState.Unavailable, WorkflowApprovalGateMetrics.MarkerMissing);
    }
}
