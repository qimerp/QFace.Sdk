using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.Inventory;

public static class InventoryGoodsReceiptSync
{
    public const string WorkflowType = "InventoryGoodsReceiptSyncWorkflow";
    public const string WorkflowTaskQueue = "qimerp-operations-inventory-goods-receipt-sync";
    public const string ActivityName = "SyncInventoryGoodsReceipt:Finance";
    public const string ActivityTaskQueue = "qimerp-finance-ap-inventory-goods-receipt-sync";

    public static string WorkflowId(string tenantId, Guid goodsReceiptId) =>
        $"inventory-goods-receipt-{tenantId}-{goodsReceiptId:N}";
}

/// <summary>Inventory → Finance AP: a posted PO-linked Inventory receipt; Finance must not raise its stock event for it.</summary>
public class InventoryGoodsReceiptSyncRequest
{
    public string TenantId { get; set; } = string.Empty;
    public InventoryGoodsReceiptData Receipt { get; set; } = new();
}

public class InventoryGoodsReceiptData
{
    public Guid GoodsReceiptId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid PurchaseOrderId { get; set; }
    public string VendorId { get; set; } = string.Empty;
    public DateTime ReceiptDate { get; set; }
    public List<InventoryGoodsReceiptLineData> Lines { get; set; } = [];
}

public class InventoryGoodsReceiptLineData
{
    public Guid PurchaseOrderLineId { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

/// <summary>Started by Inventory on <see cref="InventoryGoodsReceiptSync.WorkflowTaskQueue"/> after a PO-linked receipt posts.</summary>
[Workflow(InventoryGoodsReceiptSync.WorkflowType)]
public interface IInventoryGoodsReceiptSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(InventoryGoodsReceiptSyncRequest request);
}

/// <summary>Hosted by Finance on <see cref="InventoryGoodsReceiptSync.ActivityTaskQueue"/>; idempotent by <see cref="InventoryGoodsReceiptData.GoodsReceiptId"/>.</summary>
public interface IInventoryGoodsReceiptSyncActivity
{
    [Activity(InventoryGoodsReceiptSync.ActivityName)]
    Task SyncAsync(InventoryGoodsReceiptSyncRequest request);
}
