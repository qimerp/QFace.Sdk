namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Shared intercept-time decisions for update/delete workflow initiation.
/// </summary>
public static class WorkflowInterceptDecisions
{
    public static bool ShouldInitiateWorkflowOnUpdate(IWorkflowEnabled entity) =>
        entity.WorkflowStatus is WorkflowStatus.NotStarted
            or WorkflowStatus.Rejected
            or WorkflowStatus.Approved
            or WorkflowStatus.Returned;

    public static bool IsUpdateWorkflowBlockedByStatus(IWorkflowEnabled entity) =>
        entity.WorkflowStatus == WorkflowStatus.InProgress;

    public static bool ShouldStartCreateWorkflow(EntityWorkflowConfig config, IWorkflowEnabled entity) =>
        ConfigRequiresCreateApproval(config, entity);

    /// <remarks>Without an entity the trigger conditions cannot be evaluated and are treated as met.</remarks>
    public static bool ConfigRequiresCreateApproval(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] EntityWorkflowConfig? config,
        IWorkflowEnabled? entity) =>
        config is { EnableWorkflowForCreate: true }
        && !string.IsNullOrWhiteSpace(config.CreateWorkflowCode)
        && (entity is null || WorkflowTriggerConditionEvaluator.EvaluateAll(entity, config.CreateTriggerConditions));
}
