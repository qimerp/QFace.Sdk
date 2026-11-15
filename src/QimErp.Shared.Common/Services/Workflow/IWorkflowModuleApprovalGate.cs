namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Gates approval-workflow execution on whether the Workflow module is enabled for a
/// tenant. Implemented by <see cref="WorkflowModuleApprovalGate"/>.
/// </summary>
public interface IWorkflowModuleApprovalGate
{
    /// <summary>Returns false only when the Workflow module is known to be absent; an unreadable module state returns true.</summary>
    Task<bool> IsApprovalModuleEnabledAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <remarks>
    /// On <see cref="WorkflowApprovalGateState.Unavailable"/> throw <see cref="WorkflowApprovalStateUnavailableException"/>; never save without approval.
    /// For CREATE pass the entity's preset WorkflowCode and the entity. If the config would start create approval for the
    /// entity and the code differs, this throws <see cref="WorkflowPresetCodeMismatchException"/>; otherwise the code is
    /// checked first and falls back to the entity type's definition.
    /// The default implementation, for gates that predate this method, returns Unavailable unless the module is enabled.
    /// </remarks>
    async Task<WorkflowApprovalGateState> GetApprovalStateAsync(
        string? tenantId,
        string? module,
        string entityType,
        string operation,
        string? workflowCode = null,
        IWorkflowEnabled? entity = null,
        CancellationToken cancellationToken = default) =>
        await IsApprovalModuleEnabledAsync(tenantId, cancellationToken)
            ? WorkflowApprovalGateState.On
            : WorkflowApprovalGateState.Unavailable;
}
