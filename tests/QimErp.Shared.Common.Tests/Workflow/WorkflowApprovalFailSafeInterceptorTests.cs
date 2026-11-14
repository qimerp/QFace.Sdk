using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox.Transports;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using QimErp.Shared.Common.Workflow.Enums;
using Xunit;

namespace QimErp.Shared.Common.Tests.Workflow;

public class WorkflowApprovalFailSafeInterceptorTests
{
    private const string TenantId = "failsafe-tenant-id";
    private const string ModuleName = "TestModule";

    [Fact]
    public async Task ModuleUnknown_ConfigRequiresCreateApproval_StartsApproval()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        harness.ConfigCache.SetConfig(ModuleName, CreateApprovalConfig());
        harness.DefinitionProvider.SetDefinition("CREATE-WF", ActiveDefinition());

        var entity = NewEntity();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().Be(WorkflowStatus.InProgress);
        entity.WorkflowCode.Should().Be("CREATE-WF");
        entity.DataStatus.Should().NotBe(DataState.Active);
        (await ApprovalRowCountAsync(harness)).Should().Be(1);
    }

    [Fact]
    public async Task ModuleUnknown_NoConfig_SavesWithoutApproval()
    {
        await using var harness = Harness(ModuleAccess.Unknown);

        var entity = NewEntity();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().NotBe(WorkflowStatus.InProgress);
        entity.DataStatus.Should().Be(DataState.Active);
        (await ApprovalRowCountAsync(harness)).Should().Be(0);
        (await harness.Context.Entities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ModuleUnknown_ConfigUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        harness.ConfigCache.SetConfig(ModuleName, CreateApprovalConfig());
        harness.ConfigCache.Unreadable = true;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        var thrown = await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        thrown.Which.TenantId.Should().Be(TenantId);
        thrown.Which.Module.Should().Be(ModuleName);
        thrown.Which.EntityType.Should().Be(nameof(TestWorkflowEntity));
        thrown.Which.Operation.Should().Be("CREATE");
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ModuleEnabled_ConfigUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Enabled);
        harness.ConfigCache.SetConfig(ModuleName, CreateApprovalConfig());
        harness.ConfigCache.Unreadable = true;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ModuleNotInstalled_SavesActiveWithoutApproval_EvenWithConfig()
    {
        await using var harness = Harness(ModuleAccess.NotInstalled);
        harness.ConfigCache.SetConfig(ModuleName, CreateApprovalConfig());
        harness.DefinitionProvider.SetDefinition("CREATE-WF", ActiveDefinition());

        var entity = NewEntity();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().NotBe(WorkflowStatus.InProgress);
        entity.DataStatus.Should().Be(DataState.Active);
        (await ApprovalRowCountAsync(harness)).Should().Be(0);
    }

    [Fact]
    public async Task ModuleUnknown_UpdateRouteInConfig_StartsUpdateApproval()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        var entity = NewEntity();
        entity.EnableWorkflowAfterSeeding();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        harness.ConfigCache.SetConfig(ModuleName, new EntityWorkflowConfig
        {
            EnableWorkflowForUpdate = true,
            UpdateWorkflowRoutes = [new WorkflowOperationRoute { Priority = 1, WorkflowCode = "UPDATE-WF" }]
        });
        harness.DefinitionProvider.SetDefinition("UPDATE-WF", ActiveDefinition());

        var reloaded = await harness.Context.Entities.FirstAsync(e => e.Id == entity.Id);
        reloaded.EnableWorkflowProcessing();
        reloaded.Amount = 5000;
        await harness.Context.SaveChangesAsync();

        reloaded.WorkflowStatus.Should().Be(WorkflowStatus.InProgress);
        reloaded.WorkflowCode.Should().Be("UPDATE-WF");
    }

    private static InterceptorE2EHarness Harness(ModuleAccess access) =>
        new(TenantId, ModuleName, new ScriptedModuleAccessService(access), registerApprovalGate: true);

    private static Task<int> ApprovalRowCountAsync(InterceptorE2EHarness harness) =>
        harness.Context.OutboxMessages
            .CountAsync(r => r.EventType == WorkflowApprovalOutboxTemporalHandler.EventTypeName);

    private static TestWorkflowEntity NewEntity()
    {
        var entity = new TestWorkflowEntity { Title = "New hire", Amount = 100, Status = "Active" };
        entity.EnableWorkflowProcessing();
        entity.TenantId = TenantId;
        return entity;
    }

    private static EntityWorkflowConfig CreateApprovalConfig() =>
        new() { EnableWorkflowForCreate = true, CreateWorkflowCode = "CREATE-WF" };

    private static PublishedWorkflowDefinition ActiveDefinition() =>
        new()
        {
            TenantId = TenantId,
            EntityType = nameof(TestWorkflowEntity),
            IsActive = true,
            Definition = new WorkflowDefinition
            {
                Steps = [new WorkflowStep { StepCode = "STEP-1", Name = "STEP-1", Order = 1 }]
            }
        };
}
