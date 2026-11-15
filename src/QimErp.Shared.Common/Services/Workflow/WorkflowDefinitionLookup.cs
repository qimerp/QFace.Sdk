namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowDefinitionLookupStatus
{
    /// <summary>The definition could not be read; whether one is published is not known.</summary>
    Unknown = 0,
    Found = 1,
    Absent = 2,
}

public readonly record struct WorkflowDefinitionLookup(
    WorkflowDefinitionLookupStatus Status,
    PublishedWorkflowDefinition? Definition)
{
    public static WorkflowDefinitionLookup Unknown => default;

    public static WorkflowDefinitionLookup Absent => new(WorkflowDefinitionLookupStatus.Absent, null);

    public static WorkflowDefinitionLookup Found(PublishedWorkflowDefinition definition) =>
        new(WorkflowDefinitionLookupStatus.Found, definition);
}
