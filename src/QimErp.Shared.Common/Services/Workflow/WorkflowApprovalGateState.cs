namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowApprovalGateState
{
    /// <summary>Whether approval is needed could not be determined; refuse the write.</summary>
    Unavailable = 0,

    Off = 1,

    /// <summary>Apply the tenant's workflow configuration for the entity.</summary>
    On = 2,
}
