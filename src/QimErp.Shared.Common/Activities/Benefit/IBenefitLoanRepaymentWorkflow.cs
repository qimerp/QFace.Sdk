using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Benefit;

/// <summary>
/// Implemented by the Benefit module. Payroll starts it for each loan instalment deducted in a
/// finalised run, for each loan a finalised Final settlement run settles (even when it took nothing),
/// or for a deduction reversed afterwards, without a cross-project dependency on the Benefit WebApi.
/// The tenant travels in the Temporal tenant header, not in the payload.
/// </summary>
[Workflow("BenefitLoanRepaymentWorkflow")]
public interface IBenefitLoanRepaymentWorkflow
{
    [WorkflowRun]
    Task RunAsync(BenefitLoanRepaymentSyncRequest request);
}

public static class BenefitLoanRepaymentSync
{
    public const string TaskQueue = "qimerp-benefit-loan-repayment";
}

public class BenefitLoanRepaymentSyncRequest
{
    /// <summary>Deducted | Reversed.</summary>
    public string Action { get; set; } = string.Empty;

    public Guid LoanId { get; set; }
    public Guid PayrollRunId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public Guid SourceLineId { get; set; }

    /// <summary>Set by a Final settlement run; Amount may then be 0 and SourceLineId empty.</summary>
    public bool IsFinalSettlement { get; set; }

    /// <summary>What a Final settlement could not recover, in CurrencyCode; null unless IsFinalSettlement.</summary>
    public decimal? ShortfallAmount { get; set; }
}
