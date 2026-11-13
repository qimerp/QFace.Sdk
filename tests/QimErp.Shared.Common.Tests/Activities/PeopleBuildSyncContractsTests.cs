using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using QimErp.Shared.Common.Activities.Benefit;
using QimErp.Shared.Common.Activities.Payroll;
using QimErp.Shared.Common.Activities.TenantReference;
using QimErp.Shared.Common.Activities.Workflow;
using Temporalio.Converters;
using Temporalio.Workflows;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class PeopleBuildSyncContractsTests
{
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    [Fact]
    public void Tenant_reference_holiday_fields_round_trip_as_web_json()
    {
        var request = new TenantReferenceSyncRequest
        {
            EntityType = TenantReferenceEntityType.Holiday,
            TenantId = "tenant-a",
            EntityId = Guid.NewGuid(),
            HolidayName = "Hogbetsotso",
            HolidayDate = new DateOnly(2026, 11, 7),
            HolidayType = "Cultural",
            HolidayCountryCode = "GH",
            HolidayRegion = "Volta",
            HolidayDescription = "Anlo festival.",
            IsActive = true
        };

        var restored = RoundTrip(request);

        restored.Should().BeEquivalentTo(request);
    }

    [Fact]
    public void Benefit_sync_loan_fields_round_trip_as_web_json()
    {
        var request = new BenefitSyncRequest
        {
            Action = "LoanApproved",
            TenantId = "tenant-a",
            EmployeeId = Guid.NewGuid(),
            LoanCode = "LN-0001",
            LoanId = Guid.NewGuid(),
            InstallmentAmount = 416.67m,
            InstallmentCount = 12,
            FirstDeductionOn = new DateOnly(2026, 12, 1),
            CurrencyCode = "GHS",
            StopKind = "Settled"
        };

        var restored = RoundTrip(request);

        restored.Should().BeEquivalentTo(request);
    }

    [Fact]
    public void A_benefit_sync_payload_from_before_the_loan_fields_still_reads()
    {
        const string before = """
            {"action":"LoanRepayment","tenantId":"tenant-a","triggeredBy":"system","employeeId":"0193e2a0-0000-7000-8000-000000000001",
             "employeeCode":"E-1","employeeName":"Ama Boateng","monthlyPremium":0,"effectiveDate":"2026-01-01T00:00:00",
             "loanCode":"LN-0001","paymentAmount":200.50,"outstandingBalance":1799.50,"monthlyRent":0,"allocationDate":"0001-01-01T00:00:00"}
            """;

        var restored = JsonSerializer.Deserialize<BenefitSyncRequest>(before, Web)!;

        restored.Action.Should().Be("LoanRepayment");
        restored.PaymentAmount.Should().Be(200.50m);
        restored.OutstandingBalance.Should().Be(1799.50m);
        restored.LoanId.Should().BeNull();
        restored.InstallmentAmount.Should().Be(0m);
        restored.InstallmentCount.Should().Be(0);
        restored.FirstDeductionOn.Should().BeNull();
        restored.CurrencyCode.Should().BeNull();
        restored.StopKind.Should().BeNull();
    }

    [Fact]
    public void Benefit_loan_repayment_request_round_trips_and_carries_no_tenant()
    {
        var request = new BenefitLoanRepaymentSyncRequest
        {
            Action = "Deducted",
            LoanId = Guid.NewGuid(),
            PayrollRunId = Guid.NewGuid(),
            PeriodStart = new DateOnly(2026, 12, 1),
            Amount = 416.67m,
            CurrencyCode = "GHS",
            SourceLineId = Guid.NewGuid()
        };

        RoundTrip(request).Should().BeEquivalentTo(request);
        PropertyNames<BenefitLoanRepaymentSyncRequest>().Should().BeEquivalentTo(
            "Action", "LoanId", "PayrollRunId", "PeriodStart", "Amount", "CurrencyCode", "SourceLineId",
            "IsFinalSettlement", "ShortfallAmount");
    }

    [Fact]
    public void A_final_settlement_report_with_nothing_taken_round_trips_on_the_temporal_wire()
    {
        var request = new BenefitLoanRepaymentSyncRequest
        {
            Action = "Deducted",
            LoanId = Guid.NewGuid(),
            PayrollRunId = Guid.NewGuid(),
            PeriodStart = new DateOnly(2026, 12, 1),
            Amount = 0m,
            CurrencyCode = "GHS",
            IsFinalSettlement = true,
            ShortfallAmount = 750.25m
        };

        var restored = TemporalRoundTrip<BenefitLoanRepaymentSyncRequest, BenefitLoanRepaymentSyncRequest>(request);

        restored.Should().BeEquivalentTo(request);
        restored.IsFinalSettlement.Should().BeTrue();
        restored.ShortfallAmount.Should().Be(750.25m);
        restored.SourceLineId.Should().BeEmpty();
        RoundTrip(request).Should().BeEquivalentTo(request);
    }

    [Fact]
    public void A_repayment_report_from_a_sender_without_the_settlement_fields_reads_as_a_plain_deduction()
    {
        var before = new RepaymentBeforeSettlement
        {
            Action = "Deducted",
            LoanId = Guid.NewGuid(),
            PayrollRunId = Guid.NewGuid(),
            PeriodStart = new DateOnly(2026, 12, 1),
            Amount = 416.67m,
            CurrencyCode = "GHS",
            SourceLineId = Guid.NewGuid()
        };

        var restored = TemporalRoundTrip<RepaymentBeforeSettlement, BenefitLoanRepaymentSyncRequest>(before);

        restored.Should().BeEquivalentTo(before);
        restored.IsFinalSettlement.Should().BeFalse();
        restored.ShortfallAmount.Should().BeNull();

        var web = JsonSerializer.Deserialize<BenefitLoanRepaymentSyncRequest>(
            """{"action":"Deducted","loanId":"0193e2a0-0000-7000-8000-000000000005","payrollRunId":"0193e2a0-0000-7000-8000-000000000006","periodStart":"2026-12-01","amount":416.67,"currencyCode":"GHS","sourceLineId":"0193e2a0-0000-7000-8000-000000000007"}""",
            Web)!;
        web.Amount.Should().Be(416.67m);
        web.IsFinalSettlement.Should().BeFalse();
        web.ShortfallAmount.Should().BeNull();
    }

    [Fact]
    public void A_worker_without_the_settlement_fields_still_reads_a_settlement_report()
    {
        var request = new BenefitLoanRepaymentSyncRequest
        {
            Action = "Deducted",
            LoanId = Guid.NewGuid(),
            PayrollRunId = Guid.NewGuid(),
            PeriodStart = new DateOnly(2026, 12, 1),
            Amount = 1_000m,
            CurrencyCode = "GHS",
            SourceLineId = Guid.NewGuid(),
            IsFinalSettlement = true,
            ShortfallAmount = 0m
        };

        var restored = TemporalRoundTrip<BenefitLoanRepaymentSyncRequest, RepaymentBeforeSettlement>(request);

        restored.Should().BeEquivalentTo(request, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public void Benefit_loan_repayment_workflow_has_its_registered_name_and_queue()
    {
        WorkflowName<IBenefitLoanRepaymentWorkflow>().Should().Be("BenefitLoanRepaymentWorkflow");
        RunParameter<IBenefitLoanRepaymentWorkflow>().Should().Be(typeof(BenefitLoanRepaymentSyncRequest));
        BenefitLoanRepaymentSync.TaskQueue.Should().Be("qimerp-benefit-loan-repayment");
    }

    [Fact]
    public void Approval_delegation_request_round_trips_and_carries_no_tenant()
    {
        var request = new ApprovalDelegationSyncRequest
        {
            Operation = "Upserted",
            DelegationId = Guid.NewGuid(),
            Version = 7,
            PrincipalEmployeeId = Guid.NewGuid(),
            SubstituteEmployeeId = Guid.NewGuid(),
            StartOn = new DateOnly(2026, 12, 1),
            EndOn = new DateOnly(2026, 12, 15),
            Modules = ["Leave", "Benefit"],
            EndedAt = new DateTime(2026, 12, 9, 8, 30, 0, DateTimeKind.Utc)
        };

        var restored = RoundTrip(request);

        restored.Should().BeEquivalentTo(request);
        restored.Modules.Should().Equal("Leave", "Benefit");
        PropertyNames<ApprovalDelegationSyncRequest>().Should().BeEquivalentTo(
            "Operation", "DelegationId", "Version", "PrincipalEmployeeId", "SubstituteEmployeeId",
            "StartOn", "EndOn", "Modules", "EndedAt");
    }

    [Fact]
    public void An_approval_delegation_for_every_module_reads_an_empty_module_list()
    {
        var restored = JsonSerializer.Deserialize<ApprovalDelegationSyncRequest>(
            """{"operation":"Deleted","delegationId":"0193e2a0-0000-7000-8000-000000000002","version":3}""", Web)!;

        restored.Operation.Should().Be("Deleted");
        restored.Version.Should().Be(3);
        restored.Modules.Should().BeEmpty();
        restored.EndedAt.Should().BeNull();
    }

    [Fact]
    public void Approval_delegation_workflow_has_its_registered_name_and_queue()
    {
        WorkflowName<IApprovalDelegationSyncWorkflow>().Should().Be("ApprovalDelegationSyncWorkflow");
        RunParameter<IApprovalDelegationSyncWorkflow>().Should().Be(typeof(ApprovalDelegationSyncRequest));
        ApprovalDelegationSync.TaskQueue.Should().Be("qimerp-corehr-approval-delegation-sync");
    }

    [Fact]
    public void Benefit_sync_final_installment_round_trips_on_the_temporal_wire()
    {
        var request = new BenefitSyncRequest
        {
            Action = "LoanApproved",
            TenantId = "tenant-a",
            EmployeeId = Guid.NewGuid(),
            LoanId = Guid.NewGuid(),
            InstallmentAmount = 333.33m,
            InstallmentCount = 3,
            FinalInstallmentAmount = 333.34m,
            FirstDeductionOn = new DateOnly(2026, 12, 1),
            CurrencyCode = "GHS"
        };

        var restored = TemporalRoundTrip<BenefitSyncRequest, BenefitSyncRequest>(request);

        restored.Should().BeEquivalentTo(request);
        restored.FinalInstallmentAmount.Should().Be(333.34m);
        RoundTrip(request).FinalInstallmentAmount.Should().Be(333.34m);
    }

    [Fact]
    public void A_loan_approval_from_a_sender_without_the_final_installment_reads_as_all_equal()
    {
        var before = new LoanApprovalBeforeFinalInstallment
        {
            Action = "LoanApproved",
            TenantId = "tenant-a",
            EmployeeId = Guid.NewGuid(),
            LoanId = Guid.NewGuid(),
            InstallmentAmount = 250m,
            InstallmentCount = 4,
            FirstDeductionOn = new DateOnly(2026, 12, 1),
            CurrencyCode = "GHS"
        };

        var restored = TemporalRoundTrip<LoanApprovalBeforeFinalInstallment, BenefitSyncRequest>(before);

        restored.FinalInstallmentAmount.Should().BeNull();
        restored.LoanId.Should().Be(before.LoanId);
        restored.InstallmentAmount.Should().Be(250m);
        restored.InstallmentCount.Should().Be(4);
        restored.FirstDeductionOn.Should().Be(before.FirstDeductionOn);
        restored.CurrencyCode.Should().Be("GHS");

        JsonSerializer.Deserialize<BenefitSyncRequest>(
            """{"action":"LoanApproved","loanId":"0193e2a0-0000-7000-8000-000000000003","installmentAmount":250,"installmentCount":4}""",
            Web)!.FinalInstallmentAmount.Should().BeNull();
    }

    [Fact]
    public void A_worker_without_the_final_installment_still_reads_a_new_loan_approval()
    {
        var request = new BenefitSyncRequest
        {
            Action = "LoanApproved",
            TenantId = "tenant-a",
            EmployeeId = Guid.NewGuid(),
            LoanId = Guid.NewGuid(),
            InstallmentAmount = 333.33m,
            InstallmentCount = 3,
            FinalInstallmentAmount = 333.34m,
            FirstDeductionOn = new DateOnly(2026, 12, 1),
            CurrencyCode = "GHS"
        };

        var restored = TemporalRoundTrip<BenefitSyncRequest, LoanApprovalBeforeFinalInstallment>(request);

        restored.Should().BeEquivalentTo(request, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public void Medical_cover_sync_request_round_trips_on_the_temporal_wire_and_carries_no_tenant()
    {
        var request = new MedicalCoverSyncRequest
        {
            Action = MedicalCoverSync.EnrollmentChanged,
            SourceId = Guid.NewGuid(),
            SourceVersion = 4,
            Removed = false,
            EmployeeId = Guid.NewGuid(),
            BenefitPlanId = Guid.NewGuid(),
            EnrollmentStatus = "Suspended",
            EffectiveOn = new DateOnly(2026, 12, 1),
            EndedOn = new DateOnly(2027, 11, 30),
            IsMedical = true,
            HasEligibilityRules = true,
            EligibleAfterMonths = 3,
            CoverStart = "FirstOfNextMonth",
            GeneralWaitingDays = 30,
            MaternityWaitingMonths = 10,
            PreExistingWaitingMonths = 12,
            ChronicWaitingMonths = 6,
            AccidentsFromDayOne = true,
            HospitalName = "Korle Bu Teaching Hospital",
            HospitalActive = true
        };

        TemporalRoundTrip<MedicalCoverSyncRequest, MedicalCoverSyncRequest>(request).Should().BeEquivalentTo(request);
        RoundTrip(request).Should().BeEquivalentTo(request);
        PropertyNames<MedicalCoverSyncRequest>().Should().NotContain(name => name.Contains("Tenant"));
        PropertyNames<MedicalCoverSyncRequest>().Should().BeEquivalentTo(
            "Action", "SourceId", "SourceVersion", "Removed", "EmployeeId", "BenefitPlanId", "EnrollmentStatus",
            "EffectiveOn", "EndedOn", "IsMedical", "HasEligibilityRules", "EligibleAfterMonths", "CoverStart",
            "GeneralWaitingDays", "MaternityWaitingMonths", "PreExistingWaitingMonths", "ChronicWaitingMonths",
            "AccidentsFromDayOne", "HospitalName", "HospitalActive");
    }

    [Fact]
    public void A_hospital_change_reads_with_enrollment_fields_absent()
    {
        var restored = JsonSerializer.Deserialize<MedicalCoverSyncRequest>(
            """{"action":"HospitalChanged","sourceId":"0193e2a0-0000-7000-8000-000000000004","sourceVersion":2,"hospitalName":"Ridge Hospital","hospitalActive":false}""",
            Web)!;

        restored.Action.Should().Be(MedicalCoverSync.HospitalChanged);
        restored.SourceVersion.Should().Be(2);
        restored.HospitalActive.Should().BeFalse();
        restored.Removed.Should().BeFalse();
        restored.EmployeeId.Should().BeEmpty();
        restored.EnrollmentStatus.Should().BeNull();
        restored.EffectiveOn.Should().BeNull();
        restored.CoverStart.Should().BeNull();
    }

    [Fact]
    public void Medical_cover_sync_workflow_has_its_registered_name_queue_and_actions()
    {
        WorkflowName<IMedicalCoverSyncWorkflow>().Should().Be("MedicalCoverSyncWorkflow");
        MedicalCoverSync.WorkflowType.Should().Be("MedicalCoverSyncWorkflow");
        RunParameter<IMedicalCoverSyncWorkflow>().Should().Be(typeof(MedicalCoverSyncRequest));
        MedicalCoverSync.TaskQueue.Should().Be("qimerp-payroll-medical-cover-sync");
        MedicalCoverSync.EnrollmentChanged.Should().Be("EnrollmentChanged");
        MedicalCoverSync.PlanChanged.Should().Be("PlanChanged");
        MedicalCoverSync.HospitalChanged.Should().Be("HospitalChanged");
    }

    [Fact]
    public void Medical_cover_sync_workflow_id_names_the_action_version_and_source()
    {
        var sourceId = Guid.Parse("0193e2a0-0000-7000-8000-00000000abcd");

        MedicalCoverSync.WorkflowId(MedicalCoverSync.EnrollmentChanged, sourceId, 7)
            .Should().Be("medical-cover-sync-EnrollmentChanged-7-0193e2a000007000800000000000abcd");
        MedicalCoverSync.WorkflowId(MedicalCoverSync.PlanChanged, sourceId, 7)
            .Should().NotBe(MedicalCoverSync.WorkflowId(MedicalCoverSync.PlanChanged, sourceId, 8));
        MedicalCoverSync.WorkflowId(MedicalCoverSync.HospitalChanged, sourceId, 1)
            .Should().NotBe(MedicalCoverSync.WorkflowId(MedicalCoverSync.PlanChanged, sourceId, 1));
    }

    private sealed class LoanApprovalBeforeFinalInstallment
    {
        public string Action { get; set; } = "";
        public string TenantId { get; set; } = "";
        public Guid EmployeeId { get; set; }
        public Guid? LoanId { get; set; }
        public decimal InstallmentAmount { get; set; }
        public int InstallmentCount { get; set; }
        public DateOnly? FirstDeductionOn { get; set; }
        public string? CurrencyCode { get; set; }
    }

    private sealed class RepaymentBeforeSettlement
    {
        public string Action { get; set; } = "";
        public Guid LoanId { get; set; }
        public Guid PayrollRunId { get; set; }
        public DateOnly PeriodStart { get; set; }
        public decimal Amount { get; set; }
        public string CurrencyCode { get; set; } = "";
        public Guid SourceLineId { get; set; }
    }

    private static TOut TemporalRoundTrip<TIn, TOut>(TIn value)
    {
        var converter = DataConverter.Default.PayloadConverter;
        return converter.ToValue<TOut>(converter.ToPayload(value));
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Web), Web)!;

    private static IEnumerable<string> PropertyNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    private static string? WorkflowName<T>() => typeof(T).GetCustomAttribute<WorkflowAttribute>()?.Name;

    private static Type RunParameter<T>() =>
        typeof(T).GetMethods()
            .Single(m => m.GetCustomAttribute<WorkflowRunAttribute>() is not null)
            .GetParameters().Single().ParameterType;
}
