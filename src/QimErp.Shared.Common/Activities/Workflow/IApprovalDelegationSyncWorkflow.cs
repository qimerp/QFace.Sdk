using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Workflow;

/// <summary>
/// Implemented by Core HR. Platform starts it when an approval delegation is created, ended or
/// deleted so Leave can honour the delegation from its own mirror.
/// The tenant travels in the Temporal tenant header, not in the payload.
/// </summary>
[Workflow("ApprovalDelegationSyncWorkflow")]
public interface IApprovalDelegationSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(ApprovalDelegationSyncRequest request);
}

public static class ApprovalDelegationSync
{
    public const string TaskQueue = "qimerp-corehr-approval-delegation-sync";
}

public class ApprovalDelegationSyncRequest
{
    /// <summary>Upserted | Deleted.</summary>
    public string Operation { get; set; } = string.Empty;

    public Guid DelegationId { get; set; }

    /// <summary>The delegation's row version; a consumer ignores a payload older than the one it holds.</summary>
    public long Version { get; set; }

    public Guid PrincipalEmployeeId { get; set; }
    public Guid SubstituteEmployeeId { get; set; }
    public DateOnly StartOn { get; set; }
    public DateOnly EndOn { get; set; }

    /// <summary>Workflow module names the delegation covers; empty means every module.</summary>
    public IReadOnlyList<string> Modules { get; set; } = [];

    public DateTime? EndedAt { get; set; }
}
