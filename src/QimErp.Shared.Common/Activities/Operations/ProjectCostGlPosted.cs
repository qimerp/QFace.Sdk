using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Operations;

public static class ProjectCostGlPosted
{
    public const string WorkflowType = "ProjectCostGlPostedWorkflow";
    public const string WorkflowTaskQueue = "qimerp-finance-gl-project-cost-posted";
    public const string ActivityName = "ProjectCostGlPosted:Project";
    public const string ActivityTaskQueue = "qimerp-operations-project-gl-posted";

    public static string WorkflowId(string tenantId, Guid expenditureId) =>
        $"project-cost-gl-posted-{tenantId}-{expenditureId:N}";
}

/// <summary>Finance GL → Operations: the outcome of a <see cref="ProjectCostGlPostingRequest"/>.</summary>
public class ProjectCostGlPostedRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ExpenditureId { get; set; }
    public bool Succeeded { get; set; }
    public Guid? JournalEntryId { get; set; }
    public string? JournalNumber { get; set; }
    public DateOnly? PostedOn { get; set; }

    /// <summary>One of <see cref="ProjectCostGlPostingErrors"/> when <see cref="Succeeded"/> is false.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>A fixed message for the code; never an exception message.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>Started by Finance on <see cref="ProjectCostGlPosted.WorkflowTaskQueue"/> after a posting attempt.</summary>
[Workflow(ProjectCostGlPosted.WorkflowType)]
public interface IProjectCostGlPostedWorkflow
{
    [WorkflowRun]
    Task RunAsync(ProjectCostGlPostedRequest request);
}

/// <summary>Hosted by Operations on <see cref="ProjectCostGlPosted.ActivityTaskQueue"/>; idempotent by <see cref="ProjectCostGlPostedRequest.ExpenditureId"/>.</summary>
public interface IProjectCostGlPostedActivity
{
    [Activity(ProjectCostGlPosted.ActivityName)]
    Task ApplyAsync(ProjectCostGlPostedRequest request);
}
