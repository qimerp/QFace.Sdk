using System.Diagnostics.Metrics;

namespace QimErp.Shared.Common.Services.Workflow;

public static class WorkflowApprovalGateMetrics
{
    public const string MeterName = "QimErp.Shared.Common.Workflow";
    public const string ModuleStateUnknownInstrument = "workflow.approval_gate.module_state_unknown";

    public const string AssumedEnabled = "assumed_enabled";
    public const string ConfigFound = "config_found";
    public const string ConfigAbsent = "config_absent";
    public const string ConfigUnknown = "config_unknown";
    public const string ModuleUnresolved = "module_unresolved";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ModuleStateUnknown =
        Meter.CreateCounter<long>(
            ModuleStateUnknownInstrument,
            description: "Approval gate decisions made while the tenant module snapshot was missing or unreadable");

    internal static void RecordModuleStateUnknown(string outcome, string? module, string? entityType) =>
        ModuleStateUnknown.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("module", module ?? string.Empty),
            new KeyValuePair<string, object?>("entity_type", entityType ?? string.Empty));
}
