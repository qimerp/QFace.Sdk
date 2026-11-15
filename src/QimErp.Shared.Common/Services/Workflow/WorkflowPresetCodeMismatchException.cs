namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>Permanent: the caller set a WorkflowCode that contradicts the configured create workflow; retrying cannot succeed.</summary>
public sealed class WorkflowPresetCodeMismatchException(
    string tenantId,
    string? module,
    string entityType,
    string presetCode,
    string configuredCode)
    : Exception(
        $"{entityType} was submitted with a workflow that does not match its configured approval workflow, so nothing was saved.")
{
    public string TenantId { get; } = tenantId;
    public string? Module { get; } = module;
    public string EntityType { get; } = entityType;
    public string PresetCode { get; } = presetCode;
    public string ConfiguredCode { get; } = configuredCode;
}
