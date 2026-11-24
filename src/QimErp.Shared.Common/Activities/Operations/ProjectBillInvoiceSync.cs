using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Operations;

public static class ProjectBillInvoiceSync
{
    public const string WorkflowType = "ProjectBillInvoiceSyncWorkflow";
    public const string WorkflowTaskQueue = "qimerp-finance-ar-project-bill-invoice-sync";
    public const string ActivityName = "SyncProjectBillInvoice:Project";
    public const string ActivityTaskQueue = "qimerp-operations-project-bill-invoice-sync";

    public static string WorkflowId(string tenantId, Guid billId, DateTime updatedAt) =>
        $"project-bill-invoice-sync-{tenantId}-{billId:N}-{updatedAt.Ticks}";
}

/// <summary>Finance AR → Operations: the state of the AR invoice raised from a project bill.</summary>
public class ProjectBillInvoiceSyncRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid BillId { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public ProjectBillInvoiceStatus Status { get; set; }
    public DateOnly? DueOn { get; set; }
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }

    /// <summary>Consumers drop a request older than the one they last applied (last write wins).</summary>
    public DateTime UpdatedAt { get; set; }
}

public enum ProjectBillInvoiceStatus
{
    Draft = 0,
    Posted = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Void = 4
}

/// <summary>Started by Finance on <see cref="ProjectBillInvoiceSync.WorkflowTaskQueue"/> when an invoice with SourceType ProjectBill is created, posted, paid or voided.</summary>
[Workflow(ProjectBillInvoiceSync.WorkflowType)]
public interface IProjectBillInvoiceSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(ProjectBillInvoiceSyncRequest request);
}

/// <summary>Hosted by Operations on <see cref="ProjectBillInvoiceSync.ActivityTaskQueue"/>; idempotent, ignores unknown bills.</summary>
public interface IProjectBillInvoiceSyncActivity
{
    [Activity(ProjectBillInvoiceSync.ActivityName)]
    Task SyncAsync(ProjectBillInvoiceSyncRequest request);
}
