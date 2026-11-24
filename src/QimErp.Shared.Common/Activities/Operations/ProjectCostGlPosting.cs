using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Operations;

public static class ProjectCostGlPosting
{
    public const string WorkflowType = "ProjectCostGlPostingWorkflow";
    public const string WorkflowTaskQueue = "qimerp-operations-project-gl-posting";
    public const string ActivityName = "PostProjectCost:Gl";
    public const string ActivityTaskQueue = "qimerp-finance-gl-project-cost-posting";

    public static string WorkflowId(string tenantId, Guid expenditureId) =>
        $"project-cost-gl-posting-{tenantId}-{expenditureId:N}";
}

/// <summary>Fixed <see cref="ProjectCostGlPostedRequest.ErrorCode"/> values for a failed posting.</summary>
public static class ProjectCostGlPostingErrors
{
    public const string PeriodClosed = "Gl.PeriodClosed";
    public const string AccountInvalid = "Gl.AccountInvalid";
}

/// <summary>Operations → Finance GL: post one project cost (Dr <see cref="DebitAccountId"/> tagged with the project, Cr <see cref="CreditAccountId"/>).</summary>
public class ProjectCostGlPostingRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ExpenditureId { get; set; }
    public string ExpenditureNumber { get; set; } = string.Empty;
    public Guid ProjectId { get; set; }
    public string ProjectNumber { get; set; } = string.Empty;
    public Guid? TaskId { get; set; }
    public DateOnly PostedOn { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public Guid DebitAccountId { get; set; }
    public Guid CreditAccountId { get; set; }
    public Guid? CostCenterId { get; set; }
    public string Description { get; set; } = string.Empty;
    public ProjectCostSource Source { get; set; }
}

public enum ProjectCostSource
{
    Manual = 0,
    TimeEntry = 1
}

/// <summary>Started by Operations on <see cref="ProjectCostGlPosting.WorkflowTaskQueue"/>.</summary>
[Workflow(ProjectCostGlPosting.WorkflowType)]
public interface IProjectCostGlPostingWorkflow
{
    [WorkflowRun]
    Task RunAsync(ProjectCostGlPostingRequest request);
}

/// <summary>Hosted by Finance on <see cref="ProjectCostGlPosting.ActivityTaskQueue"/>; idempotent by <see cref="ProjectCostGlPostingRequest.ExpenditureId"/> and replies through <see cref="IProjectCostGlPostedWorkflow"/>.</summary>
public interface IProjectCostGlPostingActivity
{
    [Activity(ProjectCostGlPosting.ActivityName)]
    Task PostAsync(ProjectCostGlPostingRequest request);
}
