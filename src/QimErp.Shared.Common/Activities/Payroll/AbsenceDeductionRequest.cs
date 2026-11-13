namespace QimErp.Shared.Common.Activities.Payroll;

/// <summary>
/// Request payload for the Payroll absence-deduction Temporal worker.
/// Leave approves the absence first, then starts this workflow to create the deduction line.
/// </summary>
public class AbsenceDeductionRequest
{
    public string TenantId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string TriggeredBy { get; set; } = "";

    public Guid AbsenceId { get; set; }
    public Guid EmployeeId { get; set; }
    public int PayYear { get; set; }
    public int PayMonth { get; set; }
    public decimal Days { get; set; }
}
