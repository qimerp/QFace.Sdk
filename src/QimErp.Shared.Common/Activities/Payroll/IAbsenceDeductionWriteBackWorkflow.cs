using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Payroll;

/// <summary>
/// Temporal workflow interface for CoreHr absence-deduction write-back.
/// Implemented by AbsenceDeductionWriteBackWorkflow in QimErp.CoreHr.WebApi.
/// Task queue: "qimerp-leave-absence-deduction-writeback"
/// </summary>
[Workflow("AbsenceDeductionWriteBackWorkflow")]
public interface IAbsenceDeductionWriteBackWorkflow
{
    [WorkflowRun]
    Task RunAsync(AbsenceDeductionWriteBackRequest request);
}
