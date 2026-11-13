namespace QimErp.Shared.Common.Activities.Payroll;

/// <summary>
/// Request payload for the Payroll benefit-sync Temporal worker.
/// Covers all benefit events that affect payroll deductions/assignments:
/// EnrollmentCreated, EnrollmentUpdated, EnrollmentTerminated,
/// LoanCreated, LoanRepayment, LoanApproved, LoanStopped,
/// AccommodationAllocated, AccommodationVacated.
/// </summary>
public class BenefitSyncRequest
{
    /// <summary>
    /// Discriminator: EnrollmentCreated / EnrollmentUpdated / EnrollmentTerminated /
    /// LoanCreated / LoanRepayment / AccommodationAllocated / AccommodationVacated /
    /// LoanApproved (starts payroll recovery of the loan) / LoanStopped (ends it; see <see cref="StopKind"/>)
    /// </summary>
    public string Action { get; set; } = "";

    public string TenantId { get; set; } = "";
    public string TriggeredBy { get; set; } = "";

    // ── Employee ──────────────────────────────────────────────────────────────
    public Guid EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";

    // ── Enrollment fields ─────────────────────────────────────────────────────
    public string? EnrollmentCode { get; set; }
    public string? BenefitPlanName { get; set; }
    public decimal MonthlyPremium { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    // ── Loan fields ───────────────────────────────────────────────────────────
    public string? LoanCode { get; set; }
    public decimal PaymentAmount { get; set; }
    public decimal OutstandingBalance { get; set; }
    public Guid? LoanId { get; set; }
    public decimal InstallmentAmount { get; set; }
    public int InstallmentCount { get; set; }

    /// <summary>The last instalment carries the rounding; null means every instalment equals <see cref="InstallmentAmount"/>.</summary>
    public decimal? FinalInstallmentAmount { get; set; }

    public DateOnly? FirstDeductionOn { get; set; }
    public string? CurrencyCode { get; set; }

    /// <summary>Cancelled | Defaulted | Settled. Set on LoanStopped.</summary>
    public string? StopKind { get; set; }

    // ── Accommodation fields ──────────────────────────────────────────────────
    public string? AccommodationCode { get; set; }
    public decimal MonthlyRent { get; set; }
    public DateTime AllocationDate { get; set; }
    public DateTime? TenancyAgreementEndDate { get; set; }
    public DateTime? VacateDate { get; set; }
}
