using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Inventory;

public static class ProductTaxGroupSync
{
    public const string WorkflowType = "ProductTaxGroupSyncWorkflow";
    public const string WorkflowTaskQueue = "qimerp-operations-inventory-tax-group-sync";
    public const string ActivityName = "SyncProductTaxGroup:Accounting";
    public const string ActivityTaskQueue = "qimerp-finance-tax-group-product-sync";

    public static string WorkflowId(string tenantId, Guid productId, long sourceVersion) =>
        $"product-tax-group-{tenantId}-{productId:N}-{sourceVersion}";
}

/// <summary>Inventory → Accounting snapshot of one product's tax groups; Accounting drops a snapshot older than the one it holds.</summary>
public class ProductTaxGroupSyncRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public Guid? SalesTaxGroupId { get; set; }
    public Guid? PurchaseTaxGroupId { get; set; }
    public bool IsTaxExempt { get; set; }

    /// <summary>The product was deleted in Inventory; Accounting removes its usage row.</summary>
    public bool IsDeleted { get; set; }

    public long SourceVersion { get; set; }
}

/// <summary>Started by Inventory on <see cref="ProductTaxGroupSync.WorkflowTaskQueue"/> when a product's tax groups change.</summary>
[Workflow(ProductTaxGroupSync.WorkflowType)]
public interface IProductTaxGroupSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(ProductTaxGroupSyncRequest request);
}

/// <summary>Hosted by Accounting on <see cref="ProductTaxGroupSync.ActivityTaskQueue"/>.</summary>
public interface IProductTaxGroupSyncActivity
{
    [Activity(ProductTaxGroupSync.ActivityName)]
    Task SyncAsync(ProductTaxGroupSyncRequest request);
}
