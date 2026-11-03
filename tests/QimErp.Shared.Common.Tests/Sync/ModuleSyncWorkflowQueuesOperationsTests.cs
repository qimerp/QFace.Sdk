using FluentAssertions;
using QimErp.Shared.Common.Activities.Operations;
using QimErp.Shared.Common.Sync;
using Xunit;

namespace QimErp.Shared.Common.Tests.Sync;

public class ModuleSyncWorkflowQueuesOperationsTests
{
    [Fact]
    public void Operations_finance_parent_queues_use_qimerp_finance_prefix_only()
    {
        ModuleSyncWorkflowQueues.ProjectBillSyncParent.Should().Be("qimerp-finance-ar-project-bill-sync");
        ModuleSyncWorkflowQueues.InventoryValuationGlParent.Should().Be("qimerp-finance-gl-inventory-valuation");
        ModuleSyncWorkflowQueues.StockIssueSyncParent.Should().Be("qimerp-finance-ar-stock-issue-sync");

        ModuleSyncWorkflowQueues.ProjectBillSyncParent.Should().NotContain("accounting");
        ModuleSyncWorkflowQueues.InventoryValuationGlParent.Should().NotContain("accounting");
        ModuleSyncWorkflowQueues.StockIssueSyncParent.Should().NotContain("accounting");
    }

    [Fact]
    public void ProjectExpenditureSyncRequest_exposes_SelectedModules()
    {
        var request = new ProjectExpenditureSyncRequest
        {
            TenantId = "t1",
            SelectedModules = ["project"]
        };

        request.SelectedModules.Should().ContainSingle().Which.Should().Be("project");
    }
}
