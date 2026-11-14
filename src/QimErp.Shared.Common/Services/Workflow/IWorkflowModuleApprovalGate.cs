namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Gates approval-workflow execution on whether the Workflow module is enabled for a
/// tenant. Implemented by <see cref="WorkflowModuleApprovalGate"/>.
/// </summary>
public interface IWorkflowModuleApprovalGate
{
    /// <summary>Returns false only when the Workflow module is known to be absent; an unreadable module state returns true.</summary>
    Task<bool> IsApprovalModuleEnabledAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <remarks>On <see cref="WorkflowApprovalGateState.Unavailable"/> throw <see cref="WorkflowApprovalStateUnavailableException"/>; never save without approval.</remarks>
    async Task<WorkflowApprovalGateState> GetApprovalStateAsync(
        string? tenantId,
        string? module,
        string entityType,
        CancellationToken cancellationToken = default) =>
        await IsApprovalModuleEnabledAsync(tenantId, cancellationToken)
            ? WorkflowApprovalGateState.On
            : WorkflowApprovalGateState.Off;
}
