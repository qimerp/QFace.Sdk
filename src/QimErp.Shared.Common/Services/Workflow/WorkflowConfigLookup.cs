namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowConfigLookupStatus
{
    /// <summary>The configuration could not be read; whether one exists is not known.</summary>
    Unknown = 0,
    Found = 1,
    Absent = 2,
}

public readonly record struct WorkflowConfigLookup(WorkflowConfigLookupStatus Status, EntityWorkflowConfig? Config)
{
    public static WorkflowConfigLookup Unknown => default;

    public static WorkflowConfigLookup Absent => new(WorkflowConfigLookupStatus.Absent, null);

    public static WorkflowConfigLookup Found(EntityWorkflowConfig config) =>
        new(WorkflowConfigLookupStatus.Found, config);
}
