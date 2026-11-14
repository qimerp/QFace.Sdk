using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Outbox.Transports;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using QimErp.Shared.Common.Workflow.Enums;
using QimErp.Shared.Common.Workflow.Extensions;
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
    public async Task ModuleUnknown_NoConfig_NoDefinition_MarkerPresent_SavesWithoutApproval()
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
    public async Task ModuleUnknown_NoConfig_PublishedDefinition_StartsApproval()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        harness.DefinitionProvider.SetByEntityDefinition("CREATE-WF", ActiveDefinition());

        var entity = NewEntity();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().Be(WorkflowStatus.InProgress);
        entity.WorkflowCode.Should().Be("CREATE-WF");
        (await ApprovalRowCountAsync(harness)).Should().Be(1);
    }

    [Fact]
    public async Task ModuleUnknown_NoConfig_DefinitionUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        harness.DefinitionProvider.Unreadable = true;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ModuleEnabled_UpdateWithConfigMissing_ActiveDefinitionPublished_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Enabled);
        var entity = await SeedApprovedAsync(harness);
        harness.DefinitionProvider.SetByEntityDefinition("SOME-WF", ActiveDefinition());

        entity.Amount = 9000;
        var save = () => harness.Context.SaveChangesAsync();

        var thrown = await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        thrown.Which.Operation.Should().Be("UPDATE");
        harness.Context.ChangeTracker.Clear();
        (await harness.Context.Entities.SingleAsync()).Amount.Should().Be(100);
    }

    [Fact]
    public async Task ModuleEnabled_DeleteWithConfigMissing_DefinitionUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Enabled);
        var entity = await SeedApprovedAsync(harness);
        harness.DefinitionProvider.Unreadable = true;

        harness.Context.Remove(entity);
        var save = () => harness.Context.SaveChangesAsync();

        (await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>()).Which.Operation.Should().Be("DELETE");
        harness.Context.ChangeTracker.Clear();
        (await harness.Context.Entities.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleEnabled_UpdateWithConfigMissing_NoDefinition_MarkerPresent_SavesWithoutApproval(bool gate)
    {
        await using var harness = Harness(ModuleAccess.Enabled, gate);
        var entity = await SeedApprovedAsync(harness);

        entity.Amount = 9000;
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().Be(WorkflowStatus.Approved);
        (await ApprovalRowCountAsync(harness)).Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ModuleEnabled_UpdateOrDeleteWithConfigMissing_NoDefinition_MarkerMissing_RefusesSave(bool gate, bool delete)
    {
        await using var harness = Harness(ModuleAccess.Enabled, gate);
        var entity = await SeedApprovedAsync(harness);
        harness.ConfigCache.WarmStatus = WorkflowCacheWarmStatus.NotWarm;

        if (delete)
            harness.Context.Remove(entity);
        else
            entity.Amount = 9000;
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        harness.Context.ChangeTracker.Clear();
        (await harness.Context.Entities.SingleAsync()).Amount.Should().Be(100);
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown, true, WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(ModuleAccess.Unknown, true, WorkflowCacheWarmStatus.Unknown)]
    [InlineData(ModuleAccess.Enabled, true, WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(ModuleAccess.Enabled, false, WorkflowCacheWarmStatus.NotWarm)]
    public async Task CreateNothingBound_MarkerMissingOrUnreadable_RefusesSave(ModuleAccess access, bool gate, WorkflowCacheWarmStatus warm)
    {
        await using var harness = Harness(access, gate);
        harness.ConfigCache.WarmStatus = warm;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateWithPresetCodeUnpublished_EntityTypeDefinitionActive_DoesNotStartIt(bool gate)
    {
        await using var harness = Harness(ModuleAccess.Enabled, gate);
        harness.DefinitionProvider.SetByEntityDefinition("Y-WF", ActiveDefinition());

        var entity = NewEntity();
        entity.WorkflowCode = "X-WF";
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().NotBe(WorkflowStatus.InProgress);
        entity.DataStatus.Should().Be(DataState.Active);
        (await ApprovalRowCountAsync(harness)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateWithPresetCodeUnpublished_MarkerMissing_RefusesSave(bool gate)
    {
        await using var harness = Harness(ModuleAccess.Enabled, gate);
        harness.DefinitionProvider.SetByEntityDefinition("Y-WF", ActiveDefinition());
        harness.ConfigCache.WarmStatus = WorkflowCacheWarmStatus.NotWarm;

        var entity = NewEntity();
        entity.WorkflowCode = "X-WF";
        harness.Context.Add(entity);
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleEnabled_WorkflowModuleNotConfigured_RefusesSave(bool gate)
    {
        await using var harness = Harness(ModuleAccess.Enabled, gate, module: "");

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ModuleAccess.Enabled, true)]
    [InlineData(ModuleAccess.Unknown, true)]
    [InlineData(ModuleAccess.NotInstalled, false)]
    public async Task HandOff_DefinitionUnreadable_RefusesUnlessWorkflowKnownAbsent(ModuleAccess access, bool refused)
    {
        await using var harness = Harness(access);
        harness.ConfigCache.SetConfig(ModuleName, new EntityWorkflowConfig());
        harness.DefinitionProvider.Unreadable = true;

        var entity = NewEntity();
        entity.InitiateWorkflow("tester@qimerp.com", "emp-1", "Tester", "submitted by domain code");
        harness.Context.Add(entity);
        var save = () => harness.Context.SaveChangesAsync();

        if (refused)
        {
            await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
            (await harness.Context.Entities.CountAsync()).Should().Be(0);
        }
        else
        {
            await save();
            (await ApprovalRowCountAsync(harness)).Should().Be(0);
        }
    }

    [Fact]
    public async Task ModuleEnabled_CreateWithConfig_DefinitionUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Enabled);
        harness.ConfigCache.SetConfig(ModuleName, CreateApprovalConfig());
        harness.DefinitionProvider.SetDefinition("CREATE-WF", ActiveDefinition());
        harness.DefinitionProvider.Unreadable = true;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SynchronousSave_ModuleUnknown_ConfigUnreadable_RefusesSave()
    {
        await using var harness = Harness(ModuleAccess.Unknown);
        harness.ConfigCache.Unreadable = true;

        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChanges();

        save.Should().Throw<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MultiEntitySave_LaterEntityUnavailable_WritesNothing()
    {
        await using var harness = Harness(ModuleAccess.Enabled);
        harness.DefinitionProvider.SetDefinition("PRESET-WF", ActiveDefinition());
        harness.ConfigCache.Unreadable = true;

        var first = NewEntity();
        first.WorkflowCode = "PRESET-WF";
        harness.Context.Add(first);
        harness.Context.Add(NewEntity());
        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        (await harness.Context.Entities.CountAsync()).Should().Be(0);
        (await harness.Context.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown, 1)]
    [InlineData(ModuleAccess.Enabled, 1)]
    [InlineData(ModuleAccess.NotInstalled, 0)]
    public async Task DomainSetInProgress_HandOffEnqueuedUnlessWorkflowKnownAbsent(ModuleAccess access, int expectedRows)
    {
        await using var harness = Harness(access);

        var entity = NewEntity();
        entity.WorkflowCode = "SEEDED-WF";
        entity.InitiateWorkflow("tester@qimerp.com", "emp-1", "Tester", "submitted");
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.WorkflowStatus.Should().Be(WorkflowStatus.InProgress);
        (await ApprovalRowCountAsync(harness)).Should().Be(expectedRows);
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

    private static InterceptorE2EHarness Harness(ModuleAccess access, bool gate = true, string module = ModuleName) =>
        new(TenantId, module, new ScriptedModuleAccessService(access), registerApprovalGate: gate);

    private static async Task<TestWorkflowEntity> SeedApprovedAsync(InterceptorE2EHarness harness)
    {
        var entity = NewEntity();
        entity.EnableWorkflowAfterSeeding();
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        var reloaded = await harness.Context.Entities.FirstAsync(e => e.Id == entity.Id);
        reloaded.EnableWorkflowProcessing();
        return reloaded;
    }

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
