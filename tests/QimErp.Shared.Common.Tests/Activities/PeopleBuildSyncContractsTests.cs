using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using QimErp.Shared.Common.Activities.Benefit;
using QimErp.Shared.Common.Activities.Payroll;
using QimErp.Shared.Common.Activities.TenantReference;
using QimErp.Shared.Common.Activities.Workflow;
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
            "Action", "LoanId", "PayrollRunId", "PeriodStart", "Amount", "CurrencyCode", "SourceLineId");
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

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Web), Web)!;

    private static IEnumerable<string> PropertyNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    private static string? WorkflowName<T>() => typeof(T).GetCustomAttribute<WorkflowAttribute>()?.Name;

    private static Type RunParameter<T>() =>
        typeof(T).GetMethods()
            .Single(m => m.GetCustomAttribute<WorkflowRunAttribute>() is not null)
            .GetParameters().Single().ParameterType;
}
