namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>Transient: the same request can be retried once the shared cache is readable.</summary>
public sealed class WorkflowApprovalStateUnavailableException(
    string tenantId,
    string? module,
    string entityType,
    string operation)
    : Exception(
        $"Approval settings for {entityType} could not be read, so the change was not saved. Try again shortly.")
{
    public string TenantId { get; } = tenantId;
    public string? Module { get; } = module;
    public string EntityType { get; } = entityType;
    public string Operation { get; } = operation;
}
