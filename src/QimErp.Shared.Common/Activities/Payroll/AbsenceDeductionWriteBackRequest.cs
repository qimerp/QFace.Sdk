namespace QimErp.Shared.Common.Activities.Payroll;

/// <summary>
/// Request payload for the CoreHr absence-deduction write-back Temporal worker.
/// Payroll creates the deduction line first, then starts this workflow to stamp the adjustment id.
/// </summary>
public class AbsenceDeductionWriteBackRequest
{
    public string TenantId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string TriggeredBy { get; set; } = "";

    public Guid AbsenceId { get; set; }
    public Guid DeductionAdjustmentId { get; set; }
}
