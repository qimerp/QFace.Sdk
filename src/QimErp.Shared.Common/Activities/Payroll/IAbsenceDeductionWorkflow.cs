using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Payroll;

/// <summary>
/// Temporal workflow interface for payroll unauthorised-absence salary deduction.
/// Implemented by AbsenceDeductionWorkflow in QimErp.Payroll.Core.WebApi.
/// Task queue: "qimerp-payroll-leave-sync"
/// </summary>
[Workflow("AbsenceDeductionWorkflow")]
public interface IAbsenceDeductionWorkflow
{
    [WorkflowRun]
    Task RunAsync(AbsenceDeductionRequest request);
}
