using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Payroll;

[Workflow(MedicalCoverSync.WorkflowType)]
public interface IMedicalCoverSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(MedicalCoverSyncRequest request);
}

public static class MedicalCoverSync
{
    public const string WorkflowType = "MedicalCoverSyncWorkflow";
    public const string TaskQueue = "qimerp-payroll-medical-cover-sync";

    public const string EnrollmentChanged = "EnrollmentChanged";
    public const string PlanChanged = "PlanChanged";
    public const string HospitalChanged = "HospitalChanged";

    public static string WorkflowId(string action, Guid sourceId, long sourceVersion) =>
        $"medical-cover-sync-{action}-{sourceVersion}-{sourceId:N}";
}

/// <summary>
/// Core HR Benefit → Payroll copy of the facts a medical claim is checked against. The tenant travels
/// in the Temporal tenant header, never in the payload.
/// </summary>
public class MedicalCoverSyncRequest
{
    /// <summary>EnrollmentChanged | PlanChanged | HospitalChanged.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>BenefitEnrollment.Id, BenefitPlan.Id or HospitalLookup.Id.</summary>
    public Guid SourceId { get; set; }

    /// <summary>The source row's Version after the change; must be greater than zero.</summary>
    public long SourceVersion { get; set; }

    /// <summary>The source row was deleted.</summary>
    public bool Removed { get; set; }

    public Guid EmployeeId { get; set; }
    public Guid BenefitPlanId { get; set; }

    /// <summary>The enrolment status as Benefit stores it; Payroll treats only Active and Suspended as holding cover.</summary>
    public string? EnrollmentStatus { get; set; }

    public DateOnly? EffectiveOn { get; set; }
    public DateOnly? EndedOn { get; set; }

    /// <summary>The plan's benefit type category is Medical or Health.</summary>
    public bool IsMedical { get; set; }

    /// <summary>False when the plan has no eligibility rules: cover starts on the enrolment date with no waiting period.</summary>
    public bool HasEligibilityRules { get; set; }

    public int EligibleAfterMonths { get; set; }

    /// <summary>FirstOfNextMonth | OnEligibilityDate | OnApproval.</summary>
    public string? CoverStart { get; set; }

    public int GeneralWaitingDays { get; set; }
    public int MaternityWaitingMonths { get; set; }
    public int PreExistingWaitingMonths { get; set; }
    public int ChronicWaitingMonths { get; set; }
    public bool AccidentsFromDayOne { get; set; }

    public string? HospitalName { get; set; }
    public bool HospitalActive { get; set; }
}
