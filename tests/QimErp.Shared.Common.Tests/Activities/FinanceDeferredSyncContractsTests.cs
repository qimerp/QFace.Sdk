using System.Reflection;
using FluentAssertions;
using QimErp.Shared.Common.Activities.CompanyProfile;
using QimErp.Shared.Common.Activities.Gl;
using QimErp.Shared.Common.Activities.Inventory;
using QimErp.Shared.Common.Activities.WorkspaceLinks;
using QimErp.Shared.Common.Events;
using QimErp.Shared.Common.Sync;
using QimErp.Shared.Common.TenantSetup;
using Temporalio.Activities;
using Temporalio.Converters;
using Temporalio.Workflows;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class FinanceDeferredSyncContractsTests
{
    private static T RoundTrip<T>(T value)
    {
        var converter = DataConverter.Default.PayloadConverter;
        return converter.ToValue<T>(converter.ToPayload(value));
    }

    public static TheoryData<Type> TemporalRequestTypes => new()
    {
        typeof(ProductTaxGroupSyncRequest),
        typeof(WorkspaceLinkCommand),
        typeof(WorkspaceLinkChanged),
        typeof(CompanyProfileChanged),
        typeof(InventoryGoodsReceiptSyncRequest)
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
    public void Tax_group_reference_round_trips_and_fans_out_to_installed_inventory()
    {
        var request = new GlReferenceDataSyncRequest
        {
            EntityType = GlReferenceEntityType.TaxGroup,
            TenantId = "tenant-a",
            TaxGroupUpdated = new TaxGroupUpdatedEvent(
                Guid.NewGuid(), "VAT-STD", "Standard VAT", "Both", "NewCustomersAndItems", 21.9m, true, 4, "tenant-a")
        };

        var restored = RoundTrip(request);

        restored.EntityType.Should().Be(GlReferenceEntityType.TaxGroup);
        restored.TaxGroupUpdated.Should().BeEquivalentTo(request.TaxGroupUpdated, o => o.Excluding(e => e.OccurredOn));
        ModuleSyncRegistry.FilterByInstalled(SyncType.GlReference, [ModuleKeys.CoreAccounting, ModuleKeys.Inventory])
            .Should().ContainSingle(s => s.ModuleKey == ModuleKeys.Inventory)
            .Which.Should().Match<SyncSubscription>(s => s.TaskQueue == "qimerp-operations-inventory-gl-sync" && s.ActivitySuffix == "Inventory");
        ModuleSyncRegistry.FilterByInstalled(SyncType.GlReference, [ModuleKeys.CoreAccounting])
            .Should().NotContain(s => s.ModuleKey == ModuleKeys.Inventory);
    }

    [Fact]
    public void Product_tax_group_sync_names_are_pinned()
    {
        typeof(IProductTaxGroupSyncWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("ProductTaxGroupSyncWorkflow");
        ProductTaxGroupSync.WorkflowTaskQueue.Should().Be("qimerp-operations-inventory-tax-group-sync");
        ProductTaxGroupSync.ActivityTaskQueue.Should().Be("qimerp-finance-tax-group-product-sync");
        ActivityName(typeof(IProductTaxGroupSyncActivity)).Should().Be("SyncProductTaxGroup:Accounting");

        var request = new ProductTaxGroupSyncRequest
        {
            TenantId = "tenant-a", ProductId = Guid.NewGuid(), ProductCode = "P-1", ProductName = "Rice",
            SalesTaxGroupId = Guid.NewGuid(), IsTaxExempt = false, SourceVersion = 7
        };
        RoundTrip(request).Should().BeEquivalentTo(request);
        ProductTaxGroupSync.WorkflowId("tenant-a", request.ProductId, 7)
            .Should().NotBe(ProductTaxGroupSync.WorkflowId("tenant-b", request.ProductId, 7))
            .And.NotBe(ProductTaxGroupSync.WorkflowId("tenant-a", request.ProductId, 8));
    }

    [Fact]
    public void Workspace_link_names_are_pinned_and_payloads_round_trip()
    {
        typeof(IWorkspaceLinkCommandWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("WorkspaceLinkCommandWorkflow");
        typeof(IWorkspaceLinkSyncWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("WorkspaceLinkSyncWorkflow");
        WorkspaceLinkWorkflows.CommandTaskQueue.Should().Be("qimerp-platform-workspace-link");
        WorkspaceLinkWorkflows.SyncTaskQueue.Should().Be("qimerp-finance-workspace-link-sync");
        ActivityName(typeof(IWorkspaceLinkSyncActivity)).Should().Be("SyncWorkspaceLink:Accounting");

        var command = new WorkspaceLinkCommand
        {
            TenantId = "tenant-a", LinkId = Guid.CreateVersion7(), Action = WorkspaceLinkAction.Request,
            TargetWorkspaceDomain = "partner", ActorUserId = "user-1"
        };
        RoundTrip(command).Should().BeEquivalentTo(command);

        var changed = new WorkspaceLinkChanged
        {
            TenantId = "tenant-b", LinkId = command.LinkId, Direction = WorkspaceLinkDirection.Inbound,
            Status = WorkspaceLinkStatus.Pending, CounterpartyName = "Alpha Ltd", CounterpartyDomain = "alpha",
            RequestedAtUtc = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 12, 23, 9, 0, 0, DateTimeKind.Utc), SourceVersion = 1
        };
        var restored = RoundTrip(changed);
        restored.Should().BeEquivalentTo(changed);
        restored.CounterpartyTenantId.Should().BeNull();
        restored.Purpose.Should().Be("intercompany");
    }

    [Fact]
    public void Company_profile_sync_names_are_pinned_and_payload_round_trips()
    {
        typeof(ICompanyProfileSyncWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("CompanyProfileSyncWorkflow");
        CompanyProfileSync.TaskQueue.Should().Be("qimerp-finance-company-profile-sync");
        ActivityName(typeof(ICompanyProfileSyncActivity)).Should().Be("SyncCompanyProfile:Accounting");
        ModuleSyncRegistry.FilterByInstalled(SyncType.CompanyProfile, [ModuleKeys.CoreAccounting])
            .Should().ContainSingle()
            .Which.Should().Match<SyncSubscription>(s => s.TaskQueue == CompanyProfileSync.TaskQueue && s.ActivitySuffix == "Accounting");
        ModuleSyncRegistry.FilterByInstalled(SyncType.CompanyProfile, [ModuleKeys.CoreHR]).Should().BeEmpty();

        var change = new CompanyProfileChanged
        {
            TenantId = "tenant-a", CompanyId = Guid.NewGuid(), IsDefault = true, Code = "ACME", LegalName = "Acme Ghana Limited",
            TradingName = "Acme", Tin = "C0001234567", RegistrationNumber = "CS-1", AddressLines = ["1 Ring Road", "Osu"],
            City = "Accra", Region = "Greater Accra", Country = "Ghana", CountryCode = "GH", Phone = "+233302000000",
            Email = "accounts@acme.example", Website = "https://acme.example", LogoFileId = Guid.NewGuid(), SourceVersion = 3
        };
        var restored = RoundTrip(change);
        restored.Should().BeEquivalentTo(change);
        restored.IsActive.Should().BeTrue();
        CompanyProfileSync.WorkflowId("tenant-a", change.CompanyId, 3)
            .Should().NotBe(CompanyProfileSync.WorkflowId("tenant-b", change.CompanyId, 3))
            .And.NotBe(CompanyProfileSync.WorkflowId("tenant-a", change.CompanyId, 4));
    }

    [Fact]
    public void Inventory_goods_receipt_sync_names_are_pinned_and_payload_round_trips()
    {
        typeof(IInventoryGoodsReceiptSyncWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("InventoryGoodsReceiptSyncWorkflow");
        InventoryGoodsReceiptSync.WorkflowTaskQueue.Should().Be("qimerp-operations-inventory-goods-receipt-sync");
        InventoryGoodsReceiptSync.ActivityTaskQueue.Should().Be("qimerp-finance-ap-inventory-goods-receipt-sync");
        ActivityName(typeof(IInventoryGoodsReceiptSyncActivity)).Should().Be("SyncInventoryGoodsReceipt:Finance");

        var request = new InventoryGoodsReceiptSyncRequest
        {
            TenantId = "tenant-a",
            Receipt = new InventoryGoodsReceiptData
            {
                GoodsReceiptId = Guid.NewGuid(), ReceiptNumber = "GR-0001", PurchaseOrderId = Guid.NewGuid(), VendorId = "vendor-1",
                ReceiptDate = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc),
                Lines = [new InventoryGoodsReceiptLineData { PurchaseOrderLineId = Guid.NewGuid(), ProductId = "product-1", Quantity = 12.5m, UnitCost = 3.75m }]
            }
        };
        RoundTrip(request).Should().BeEquivalentTo(request);
        InventoryGoodsReceiptSync.WorkflowId("tenant-a", request.Receipt.GoodsReceiptId)
            .Should().NotBe(InventoryGoodsReceiptSync.WorkflowId("tenant-b", request.Receipt.GoodsReceiptId));
    }

    [Fact]
    public void On_order_payload_without_action_reads_as_opened_and_released_round_trips()
    {
        var converter = DataConverter.Default.PayloadConverter;
        var legacy = converter.ToPayload(new { TenantId = "tenant-a", OnOrder = new { PurchaseOrderId = Guid.NewGuid(), PurchaseOrderCode = "PO-1" } });

        converter.ToValue<StockOnOrderSyncRequest>(legacy).OnOrder.Action.Should().Be(PurchaseOrderOnOrderAction.Opened);
        new PurchaseOrderOnOrderData().Action.Should().Be(PurchaseOrderOnOrderAction.Opened);

        var released = new StockOnOrderSyncRequest
        {
            TenantId = "tenant-a",
            OnOrder = new PurchaseOrderOnOrderData { PurchaseOrderId = Guid.NewGuid(), PurchaseOrderCode = "PO-2", Action = PurchaseOrderOnOrderAction.Released }
        };
        RoundTrip(released).OnOrder.Action.Should().Be(PurchaseOrderOnOrderAction.Released);
    }

    private static string ActivityName(Type activity) =>
        activity.GetMethods().Single().GetCustomAttribute<ActivityAttribute>()!.Name!;
}
