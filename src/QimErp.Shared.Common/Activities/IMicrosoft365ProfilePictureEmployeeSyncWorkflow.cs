using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities;

public static class Microsoft365ProfilePictureEmployeeSync
{
    public const string TaskQueue = "qimerp-microsoft365-profile-photo-sync";
    public const string WorkflowType = "Microsoft365ProfilePictureEmployeeSyncWorkflow";

    public static string WorkflowId(Guid employeeId) => $"m365-profile-photo-employee-{employeeId:N}";
}

/// <summary>
/// Pulls one employee photo from Microsoft 365 when enabled; HR update fans out via EmployeeSyncWorkflow.
/// Implemented in QimErp.CoreHr.WebApi.
/// </summary>
[Workflow(Microsoft365ProfilePictureEmployeeSync.WorkflowType)]
public interface IMicrosoft365ProfilePictureEmployeeSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(Guid employeeId);
}