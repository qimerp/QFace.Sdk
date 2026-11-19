using System.Diagnostics.Metrics;

namespace QimErp.Shared.Common.Services.Workflow;

public static class WorkflowApprovalGateMetrics
{
    public const string MeterName = "QimErp.Shared.Common.Workflow";
    public const string ModuleStateUnknownInstrument = "workflow.approval_gate.module_state_unknown";
    public const string StateUnreadableInstrument = "workflow.approval_gate.state_unreadable";
    public const string PresetCodeMismatchInstrument = "workflow.approval_gate.preset_code_mismatch";

    public const string AssumedEnabled = "assumed_enabled";
    public const string ConfigFound = "config_found";
    public const string ConfigUnknown = "config_unknown";
    public const string DefinitionFound = "definition_found";
    public const string DefinitionUnknown = "definition_unknown";
    public const string NothingBound = "nothing_bound";
    public const string MarkerMissing = "marker_missing";
    public const string BoundKeyMissing = "bound_key_missing";
    public const string PresetCodeUnpublished = "preset_code_unpublished";
    public const string ModuleUnresolved = "module_unresolved";

    public const string ConfigSource = "config";
    public const string DefinitionSource = "definition";
    public const string ConfigMissingSource = "config_missing";
    public const string TenantMissingSource = "tenant_missing";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ModuleStateUnknown =
        Meter.CreateCounter<long>(
            ModuleStateUnknownInstrument,
            description: "Approval gate decisions made while the tenant module snapshot was missing or unreadable");
    private static readonly Counter<long> StateUnreadable =
        Meter.CreateCounter<long>(
            StateUnreadableInstrument,
            description: "Writes refused because workflow configuration or definitions could not be read");
    private static readonly Counter<long> PresetCodeMismatches =
        Meter.CreateCounter<long>(
            PresetCodeMismatchInstrument,
            description: "Creates refused because their preset WorkflowCode differs from the configured create workflow");

    internal static void RecordModuleStateUnknown(string outcome, string? module, string? entityType) =>
        ModuleStateUnknown.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("module", module ?? string.Empty),
            new KeyValuePair<string, object?>("entity_type", entityType ?? string.Empty));

    internal static void RecordStateUnreadable(string source, string? module, string? entityType, string operation) =>
        StateUnreadable.Add(
            1,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("module", module ?? string.Empty),
            new KeyValuePair<string, object?>("entity_type", entityType ?? string.Empty),
            new KeyValuePair<string, object?>("operation", operation));

    internal static void RecordPresetCodeMismatch(string? module, string? entityType, string operation) =>
        PresetCodeMismatches.Add(
            1,
            new KeyValuePair<string, object?>("module", module ?? string.Empty),
            new KeyValuePair<string, object?>("entity_type", entityType ?? string.Empty),
            new KeyValuePair<string, object?>("operation", operation));
}
