using System.Reflection;
using FluentAssertions;
using QimErp.Shared.Common.Activities.Operations;
using QimErp.Shared.Common.Events;
using Temporalio.Activities;
using Temporalio.Converters;
using Temporalio.Workflows;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class ProjectsDesignContractsTests
{
    private static T RoundTrip<T>(T value)
    {
        var converter = DataConverter.Default.PayloadConverter;
        return converter.ToValue<T>(converter.ToPayload(value));
    }

    public static TheoryData<Type> TemporalRequestTypes => new()
    {
        typeof(ProjectBillInvoiceSyncRequest),
        typeof(ProjectCostGlPostingRequest),
        typeof(ProjectCostGlPostedRequest)
    };

    [Theory]
    [MemberData(nameof(TemporalRequestTypes))]
    public void Every_new_request_carries_a_non_null_string_tenant(Type type)
    {
        var property = type.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));
        new NullabilityInfoContext().Create(property).WriteState.Should().Be(NullabilityState.NotNull);
        property.GetValue(Activator.CreateInstance(type)).Should().Be(string.Empty);
    }

    [Fact]
    public void Bill_invoice_sync_names_are_pinned_and_payload_round_trips()
    {
        typeof(IProjectBillInvoiceSyncWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("ProjectBillInvoiceSyncWorkflow");
        ProjectBillInvoiceSync.WorkflowTaskQueue.Should().Be("qimerp-finance-ar-project-bill-invoice-sync");
        ProjectBillInvoiceSync.ActivityTaskQueue.Should().Be("qimerp-operations-project-bill-invoice-sync");
        ActivityName(typeof(IProjectBillInvoiceSyncActivity)).Should().Be("SyncProjectBillInvoice:Project");

        var request = new ProjectBillInvoiceSyncRequest
        {
            TenantId = "tenant-a",
            BillId = Guid.NewGuid(),
            InvoiceId = Guid.NewGuid(),
            InvoiceCode = "INV-00012",
            Status = ProjectBillInvoiceStatus.PartiallyPaid,
            DueOn = new DateOnly(2026, 12, 24),
            AmountDue = 12_627.80m,
            AmountPaid = 5_000.25m,
            UpdatedAt = new DateTime(2026, 11, 24, 10, 30, 0, DateTimeKind.Utc)
        };
        RoundTrip(request).Should().BeEquivalentTo(request);

        var later = request.UpdatedAt.AddSeconds(1);
        ProjectBillInvoiceSync.WorkflowId("tenant-a", request.BillId, request.UpdatedAt)
            .Should().NotBe(ProjectBillInvoiceSync.WorkflowId("tenant-b", request.BillId, request.UpdatedAt))
            .And.NotBe(ProjectBillInvoiceSync.WorkflowId("tenant-a", request.BillId, later));
    }

    [Fact]
    public void Cost_gl_posting_names_are_pinned_and_payload_round_trips()
    {
        typeof(IProjectCostGlPostingWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("ProjectCostGlPostingWorkflow");
        ProjectCostGlPosting.WorkflowTaskQueue.Should().Be("qimerp-operations-project-gl-posting");
        ProjectCostGlPosting.ActivityTaskQueue.Should().Be("qimerp-finance-gl-project-cost-posting");
        ActivityName(typeof(IProjectCostGlPostingActivity)).Should().Be("PostProjectCost:Gl");
        ProjectCostGlPostingErrors.PeriodClosed.Should().Be("Gl.PeriodClosed");
        ProjectCostGlPostingErrors.AccountInvalid.Should().Be("Gl.AccountInvalid");

        var request = new ProjectCostGlPostingRequest
        {
            TenantId = "tenant-a",
            ExpenditureId = Guid.NewGuid(),
            ExpenditureNumber = "EXP-00042",
            ProjectId = Guid.NewGuid(),
            ProjectNumber = "PRJ-00007",
            TaskId = Guid.NewGuid(),
            PostedOn = new DateOnly(2026, 11, 24),
            Amount = 9_975.50m,
            CurrencyCode = "GHS",
            DebitAccountId = Guid.NewGuid(),
            CreditAccountId = Guid.NewGuid(),
            CostCenterId = null,
            Description = "Labour 28.5 h",
            Source = ProjectCostSource.TimeEntry
        };
        RoundTrip(request).Should().BeEquivalentTo(request);
        ProjectCostGlPosting.WorkflowId("tenant-a", request.ExpenditureId)
            .Should().NotBe(ProjectCostGlPosting.WorkflowId("tenant-b", request.ExpenditureId));
    }

    [Fact]
    public void Cost_gl_posted_reply_names_are_pinned_and_both_outcomes_round_trip()
    {
        typeof(IProjectCostGlPostedWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("ProjectCostGlPostedWorkflow");
        ProjectCostGlPosted.WorkflowTaskQueue.Should().Be("qimerp-finance-gl-project-cost-posted");
        ProjectCostGlPosted.ActivityTaskQueue.Should().Be("qimerp-operations-project-gl-posted");
        ActivityName(typeof(IProjectCostGlPostedActivity)).Should().Be("ProjectCostGlPosted:Project");

        var posted = new ProjectCostGlPostedRequest
        {
            TenantId = "tenant-a",
            ExpenditureId = Guid.NewGuid(),
            Succeeded = true,
            JournalEntryId = Guid.NewGuid(),
            JournalNumber = "JNL-00310",
            PostedOn = new DateOnly(2026, 11, 24)
        };
        RoundTrip(posted).Should().BeEquivalentTo(posted);

        var failed = new ProjectCostGlPostedRequest
        {
            TenantId = "tenant-a",
            ExpenditureId = posted.ExpenditureId,
            Succeeded = false,
            ErrorCode = ProjectCostGlPostingErrors.PeriodClosed,
            ErrorMessage = "The accounting period is closed."
        };
        RoundTrip(failed).Should().BeEquivalentTo(failed);
        ProjectCostGlPosted.WorkflowId("tenant-a", posted.ExpenditureId)
            .Should().NotBe(ProjectCostGlPosted.WorkflowId("tenant-b", posted.ExpenditureId));
    }

    [Fact]
    public void Bill_generated_event_carries_contract_terms_and_hour_lines()
    {
        var generated = ProjectBillGeneratedEvent.Create(
            Guid.NewGuid(), "PRJ-00007", "Depot fit-out", Guid.NewGuid(), "PB-00003",
            Guid.NewGuid(), "Acme Ltd", new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), "GHS", 9_975m, "tenant-a",
            [new ProjectBillLineData { LineId = Guid.NewGuid(), Description = "Consulting", Quantity = 28.5m, UnitPrice = 350m, LineTotal = 9_975m, IsHours = true }]);
        generated.ContractId = Guid.NewGuid();
        generated.ContractNumber = "PC-00002";
        generated.PaymentTermDays = 45;
        generated.PeriodLabel = "Nov 2026";

        var restored = RoundTrip(new ProjectBillSyncRequest { TenantId = "tenant-a", Generated = generated }).Generated;

        restored.Should().BeEquivalentTo(generated, o => o.Excluding(e => e.OccurredOn));
        restored.Lines.Single().IsHours.Should().BeTrue();
    }

    [Fact]
    public void Bill_generated_payload_from_before_4_4_reads_without_contract_terms()
    {
        var converter = DataConverter.Default.PayloadConverter;
        var legacy = converter.ToPayload(new
        {
            TenantId = "tenant-a",
            Generated = new { BillId = Guid.NewGuid(), BillNumber = "PB-1", Lines = new[] { new { LineId = Guid.NewGuid(), Quantity = 2m } } }
        });

        var restored = converter.ToValue<ProjectBillSyncRequest>(legacy).Generated;

        restored.ContractId.Should().BeNull();
        restored.PaymentTermDays.Should().BeNull();
        restored.PeriodLabel.Should().BeNull();
        restored.Lines.Single().IsHours.Should().BeFalse();
    }

    [Fact]
    public void Ap_bill_posted_event_carries_journal_and_expense_account()
    {
        var expenseAccountId = Guid.NewGuid();
        var posted = ApBillPostedEvent.Create(
            Guid.NewGuid(), "BILL-00009", "vendor-1", "Supplies Co", new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), "GHS", 1_200m, "tenant-a",
            [new ApBillPostedLineData { LineId = Guid.NewGuid(), ProductName = "Cable", Quantity = 4, UnitPrice = 300m, LineTotal = 1_200m, ProjectId = Guid.NewGuid(), ExpenseAccountId = expenseAccountId }]);
        posted.JournalEntryId = Guid.NewGuid();
        posted.JournalNumber = "JNL-00311";

        var restored = RoundTrip(new ProjectExpenditureSyncRequest { TenantId = "tenant-a", Posted = posted }).Posted;

        restored.Should().BeEquivalentTo(posted, o => o.Excluding(e => e.OccurredOn));
        restored.Lines.Single().ExpenseAccountId.Should().Be(expenseAccountId);
    }

    private static string ActivityName(Type activity) =>
        activity.GetMethods().Single().GetCustomAttribute<ActivityAttribute>()!.Name!;
}
