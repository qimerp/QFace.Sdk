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
        string? workflowCode = null,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            moduleAccess, configCache, definitions, logger, tenantId, module, entityType, workflowCode, UnspecifiedOperation, cancellationToken);

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
            logger.LogDebug(
                "[WorkflowModuleApprovalGate] Empty tenant — approval workflows disabled for {EntityType}",
                entityType);
            return WorkflowApprovalGateState.Off;
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
                "[WorkflowModuleApprovalGate] No Workflow:Module is configured for {EntityType} (tenant {TenantId}, module state {ModuleAccess}) — refusing the write",
                entityType, tenantId, access);
            if (moduleUnknown)
                WorkflowApprovalGateMetrics.RecordModuleStateUnknown(WorkflowApprovalGateMetrics.ModuleUnresolved, module, entityType);
            else
                RecordRefusal(WorkflowApprovalGateMetrics.ModuleUnresolved);
            return WorkflowApprovalGateState.Unavailable;
        }

        var lookup = await configCache.LookupEntityConfigAsync(module, entityType, tenantId);
        if (lookup.Status == WorkflowConfigLookupStatus.Found)
        {
            if (moduleUnknown)
                Warn("workflow configuration found — applying it", WorkflowApprovalGateMetrics.ConfigFound);
            return WorkflowApprovalGateState.On;
        }

        if (lookup.Status == WorkflowConfigLookupStatus.Unknown)
        {
            Warn("workflow configuration unreadable — refusing the write", WorkflowApprovalGateMetrics.ConfigUnknown);
            RecordRefusal(WorkflowApprovalGateMetrics.ConfigSource);
            return WorkflowApprovalGateState.Unavailable;
        }

        var bound = await FindBoundDefinitionAsync(definitions, tenantId, entityType, workflowCode, cancellationToken);
        if (bound.Status == WorkflowDefinitionLookupStatus.Unknown)
        {
            Warn("published definition unreadable — refusing the write", WorkflowApprovalGateMetrics.DefinitionUnknown);
            RecordRefusal(WorkflowApprovalGateMetrics.DefinitionSource);
            return WorkflowApprovalGateState.Unavailable;
        }

        if (bound.Status == WorkflowDefinitionLookupStatus.Found)
        {
            if (moduleUnknown)
                Warn("no configuration but an active published definition — applying it", WorkflowApprovalGateMetrics.DefinitionFound);
            return WorkflowApprovalGateState.On;
        }

        var state = await ResolveNothingBoundAsync(configCache, logger, tenantId, module, entityType, operation);
        if (state == WorkflowApprovalGateState.Off)
            Warn("nothing bound and the tenant cache is warm — saving without approval", WorkflowApprovalGateMetrics.NothingBound);
        else
            Warn("nothing bound and the warm marker is missing — refusing the write", WorkflowApprovalGateMetrics.MarkerMissing);
        return state;

        void Warn(string reason, string outcome)
        {
            if (!moduleUnknown)
                return;

            logger.LogWarning(
                "[WorkflowModuleApprovalGate] Module state unknown for tenant {TenantId}, {Module}/{EntityType}: {Reason}",
                tenantId, module, entityType, reason);
            WorkflowApprovalGateMetrics.RecordModuleStateUnknown(outcome, module, entityType);
        }

        void RecordRefusal(string source)
        {
            if (!moduleUnknown)
                WorkflowApprovalGateMetrics.RecordStateUnreadable(source, module, entityType, operation);
        }
    }

    /// <summary>
    /// Decides an operation whose workflow configuration key is absent. CREATE follows an active published definition
    /// (the entity's own WorkflowCode when it carries one); UPDATE/DELETE with an active definition cannot be routed and
    /// are refused; nothing bound is trusted only when the tenant warm marker is present.
    /// </summary>
    internal static async Task<WorkflowApprovalGateState> ResolveWithoutConfigAsync(
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
            return WorkflowApprovalGateState.Unavailable;
        }

        if (bound.Status == WorkflowDefinitionLookupStatus.Absent)
            return await ResolveNothingBoundAsync(configCache, logger, tenantId, module, entityType, operation);

        if (operation == "CREATE")
            return WorkflowApprovalGateState.On;

        logger.LogError(
            "[WorkflowModuleApprovalGate] Workflow configuration is missing for tenant {TenantId}, {Module}/{EntityType} but an active workflow definition is published — refusing {Operation} until Platform rewrites the configuration",
            tenantId, module, entityType, operation);
        WorkflowApprovalGateMetrics.RecordStateUnreadable(
            WorkflowApprovalGateMetrics.ConfigMissingSource, module, entityType, operation);
        return WorkflowApprovalGateState.Unavailable;
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

        var lookup = string.IsNullOrWhiteSpace(workflowCode)
            ? await definitions.LookupPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken)
            : await definitions.LookupPublishedDefinitionAsync(tenantId, workflowCode, entityType, cancellationToken);

        if (lookup.Status == WorkflowDefinitionLookupStatus.Unknown)
            return WorkflowDefinitionLookup.Unknown;

        return lookup.Definition is { IsActive: true } definition
            ? WorkflowDefinitionLookup.Found(definition)
            : WorkflowDefinitionLookup.Absent;
    }

    private static async Task<WorkflowApprovalGateState> ResolveNothingBoundAsync(
        IWorkflowConfigCacheService configCache,
        ILogger logger,
        string tenantId,
        string? module,
        string entityType,
        string operation)
    {
        var warm = await configCache.GetTenantWarmStatusAsync(tenantId);
        if (warm == WorkflowCacheWarmStatus.Warm)
            return WorkflowApprovalGateState.Off;

        logger.LogWarning(
            "[WorkflowModuleApprovalGate] Nothing is bound for tenant {TenantId}, {Module}/{EntityType} {Operation}, but the workflow warm marker is {WarmStatus} — refusing the write",
            tenantId, module, entityType, operation, warm);
        WorkflowApprovalGateMetrics.RecordStateUnreadable(
            WorkflowApprovalGateMetrics.MarkerMissing, module, entityType, operation);
        return WorkflowApprovalGateState.Unavailable;
    }

    private const string UnspecifiedOperation = "unspecified";
}
