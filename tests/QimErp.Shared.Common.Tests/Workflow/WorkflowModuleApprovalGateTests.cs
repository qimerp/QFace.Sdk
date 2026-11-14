using System.Reflection;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using QFace.Sdk.RedisCache.Services;
using RedisCacheService = QimErp.Shared.Common.Services.Cache.RedisCacheService;
using QimErp.Shared.Common.Constants;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Options;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.TenantSetup;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using Xunit;

namespace QimErp.Shared.Common.Tests.Workflow;

public class WorkflowModuleApprovalGateTests
{
    private const string TenantId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string Module = "HR";
    private const string EntityType = "Employee";

    [Fact]
    public async Task ModuleNotInstalled_IsOffWithoutReadingConfig()
    {
        var config = new FakeLookupConfigCache(WorkflowConfigLookup.Unknown);

        (await Gate(ModuleAccess.NotInstalled, config).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Off);
        config.LookupCount.Should().Be(0);
    }

    [Theory]
    [InlineData(ModuleAccess.Enabled)]
    [InlineData(ModuleAccess.Unknown)]
    public async Task NothingBound_MarkerPresent_IsOff(ModuleAccess access)
    {
        var gate = Gate(access, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, WorkflowCacheWarmStatus.Warm),
            Definitions(WorkflowDefinitionLookupStatus.Absent));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Theory]
    [InlineData(ModuleAccess.Enabled, WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(ModuleAccess.Enabled, WorkflowCacheWarmStatus.Unknown)]
    [InlineData(ModuleAccess.Unknown, WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(ModuleAccess.Unknown, WorkflowCacheWarmStatus.Unknown)]
    public async Task NothingBound_MarkerMissingOrUnreadable_IsUnavailable(ModuleAccess access, WorkflowCacheWarmStatus warm)
    {
        var gate = Gate(access, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, warm),
            Definitions(WorkflowDefinitionLookupStatus.Absent));
        using var metrics = new GateMetricRecorder();

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
        metrics.Unreadable.Should().Contain(m => m.Source == WorkflowApprovalGateMetrics.MarkerMissing && m.EntityType == EntityType);
        if (access == ModuleAccess.Unknown)
            metrics.Outcomes.Should().Contain((WorkflowApprovalGateMetrics.MarkerMissing, Module, EntityType));
    }

    [Theory]
    [InlineData(WorkflowCacheWarmStatus.Warm, WorkflowApprovalGateState.Off)]
    [InlineData(WorkflowCacheWarmStatus.NotWarm, WorkflowApprovalGateState.Unavailable)]
    public async Task InactiveDefinition_CountsAsNothingBound(WorkflowCacheWarmStatus warm, WorkflowApprovalGateState expected)
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, warm),
            new FixedDefinitionProvider(WorkflowDefinitionLookup.Absent, WorkflowDefinitionLookup.Found(Published(active: false))));
        using var metrics = new GateMetricRecorder();

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(expected);
        metrics.Outcomes.Should().NotContain(o => o.Outcome == WorkflowApprovalGateMetrics.DefinitionFound && o.EntityType == EntityType);
    }

    [Theory]
    [InlineData(ModuleAccess.Enabled)]
    [InlineData(ModuleAccess.Unknown)]
    public async Task CreateWithPresetCode_CodeActiveIsOn_CodeMissingWithEntityTypeActiveIsRefused(ModuleAccess access)
    {
        var codeFound = Gate(access, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, WorkflowCacheWarmStatus.NotWarm),
            new FixedDefinitionProvider(WorkflowDefinitionLookup.Found(Published(code: "X-WF")), WorkflowDefinitionLookup.Absent));
        var codeMissingEntityTypeActive = Gate(access, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, WorkflowCacheWarmStatus.Warm),
            new FixedDefinitionProvider(WorkflowDefinitionLookup.Absent, WorkflowDefinitionLookup.Found(Published())));

        (await codeFound.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE", "X-WF")).Should().Be(WorkflowApprovalGateState.On);
        (await codeMissingEntityTypeActive.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE", "X-WF"))
            .Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Theory]
    [InlineData(WorkflowConfigLookupStatus.Found, WorkflowApprovalGateState.On)]
    [InlineData(WorkflowConfigLookupStatus.Unknown, WorkflowApprovalGateState.Unavailable)]
    public async Task ModuleEnabled_ConfigDecides(WorkflowConfigLookupStatus status, WorkflowApprovalGateState expected)
    {
        var lookup = status == WorkflowConfigLookupStatus.Found
            ? WorkflowConfigLookup.Found(new EntityWorkflowConfig())
            : WorkflowConfigLookup.Unknown;
        using var metrics = new GateMetricRecorder();

        (await Gate(ModuleAccess.Enabled, new FakeLookupConfigCache(lookup, WorkflowCacheWarmStatus.NotWarm))
                .GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(expected);
        if (status == WorkflowConfigLookupStatus.Unknown)
            metrics.Unreadable.Should().Contain(m => m.Source == WorkflowApprovalGateMetrics.ConfigSource && m.EntityType == EntityType);
    }

    [Fact]
    public async Task ModuleEnabled_NoWorkflowModuleName_IsUnavailableAndLogsError()
    {
        var logger = new CapturingLogger<WorkflowModuleApprovalGate>();
        var gate = new WorkflowModuleApprovalGate(
            new ScriptedModuleAccessService(ModuleAccess.Enabled), new FakeLookupConfigCache(WorkflowConfigLookup.Found(new EntityWorkflowConfig())), logger);

        (await gate.GetApprovalStateAsync(TenantId, " ", EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(EntityType));
    }

    [Fact]
    public async Task ModuleUnknown_ConfigFound_TurnsApprovalOn()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(
            WorkflowConfigLookup.Found(new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" })));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.On);
    }

    [Theory]
    [InlineData(WorkflowDefinitionLookupStatus.Found, WorkflowApprovalGateState.On)]
    [InlineData(WorkflowDefinitionLookupStatus.Absent, WorkflowApprovalGateState.Off)]
    [InlineData(WorkflowDefinitionLookupStatus.Unknown, WorkflowApprovalGateState.Unavailable)]
    public async Task ModuleUnknown_ConfigAbsent_PublishedDefinitionDecides(
        WorkflowDefinitionLookupStatus definition, WorkflowApprovalGateState expected)
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Absent), Definitions(definition));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(expected);
    }

    [Fact]
    public async Task ModuleUnknown_ConfigAbsent_NoDefinitionProviderRegistered_IsOff()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Absent));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Fact]
    public async Task ModuleUnknown_NoWorkflowModuleName_LogsError()
    {
        var logger = new CapturingLogger<WorkflowModuleApprovalGate>();
        var gate = new WorkflowModuleApprovalGate(
            new ScriptedModuleAccessService(ModuleAccess.Unknown), new FakeLookupConfigCache(WorkflowConfigLookup.Absent), logger);

        await gate.GetApprovalStateAsync(TenantId, null, EntityType, "CREATE");

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(EntityType));
    }

    [Fact]
    public async Task ModuleUnknown_ConfigUnknown_IsUnavailable()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Unknown));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ModuleUnknown_NoWorkflowModuleName_IsUnavailable(string? module)
    {
        var config = new FakeLookupConfigCache(WorkflowConfigLookup.Absent);
        var gate = Gate(ModuleAccess.Unknown, config);

        (await gate.GetApprovalStateAsync(TenantId, module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
        config.LookupCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyTenant_IsUnavailable_AndHandOffIsSkipped()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Unknown));

        (await gate.GetApprovalStateAsync(" ", Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
        (await gate.IsApprovalModuleEnabledAsync(null)).Should().BeFalse();
    }

    [Theory]
    [InlineData(ModuleAccess.Enabled, true)]
    [InlineData(ModuleAccess.NotInstalled, false)]
    [InlineData(ModuleAccess.Unknown, true)]
    public async Task IsApprovalModuleEnabled_OnlyFalseWhenKnownNotInstalled(ModuleAccess access, bool expected)
    {
        var gate = Gate(access, new FakeLookupConfigCache(WorkflowConfigLookup.Unknown));

        (await gate.IsApprovalModuleEnabledAsync(TenantId)).Should().Be(expected);
    }

    [Theory]
    [InlineData(WorkflowConfigLookupStatus.Found, WorkflowApprovalGateMetrics.ConfigFound)]
    [InlineData(WorkflowConfigLookupStatus.Absent, WorkflowApprovalGateMetrics.NothingBound)]
    [InlineData(WorkflowConfigLookupStatus.Unknown, WorkflowApprovalGateMetrics.ConfigUnknown)]
    public async Task ModuleUnknown_LogsWarningAndRecordsMetric(WorkflowConfigLookupStatus status, string outcome)
    {
        var lookup = status switch
        {
            WorkflowConfigLookupStatus.Found => WorkflowConfigLookup.Found(new EntityWorkflowConfig()),
            WorkflowConfigLookupStatus.Absent => WorkflowConfigLookup.Absent,
            _ => WorkflowConfigLookup.Unknown
        };
        var logger = new CapturingLogger<WorkflowModuleApprovalGate>();
        var gate = new WorkflowModuleApprovalGate(
            new ScriptedModuleAccessService(ModuleAccess.Unknown), new FakeLookupConfigCache(lookup), logger);
        using var metrics = new GateMetricRecorder();

        await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE");

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(TenantId)
            && e.Message.Contains(Module)
            && e.Message.Contains(EntityType));
        metrics.Outcomes.Should().Contain((outcome, Module, EntityType));
    }

    [Fact]
    public async Task RealServices_SnapshotMissing_ConfigWritten_TurnsApprovalOn()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.Configuration(TenantId, Module, EntityType),
            new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" });

        var gate = RealGate(redis);

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.On);
    }

    [Fact]
    public async Task RealServices_SnapshotMissing_ConfigExpired_DefinitionPublished_TurnsApprovalOn()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.PublishedDefinitionByEntityType(TenantId, EntityType),
            new PublishedWorkflowDefinition { TenantId = TenantId, WorkflowCode = "WF", EntityType = EntityType });

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.On);
    }

    [Fact]
    public async Task RealServices_SnapshotMissing_NothingPublished_MarkerPresent_IsOff()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId),
            new WorkflowTenantWarmMarker { WarmedAt = DateTimeOffset.UtcNow, Bound = [] });

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Fact]
    public async Task RealServices_SnapshotMissing_NothingPublished_MarkerMissing_IsUnavailable()
    {
        (await RealGate(new InMemoryGlobalRedis()).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Fact]
    public async Task WarmMarker_WarmNotWarmAndUnknown()
    {
        var redis = new InMemoryGlobalRedis();
        var cache = ConfigCache(redis);

        (await cache.GetTenantWarmStatusAsync(Module, EntityType, TenantId)).Should().Be(WorkflowCacheWarmStatus.NotWarm);
        redis.Down = true;
        (await cache.GetTenantWarmStatusAsync(Module, EntityType, TenantId)).Should().Be(WorkflowCacheWarmStatus.Unknown);
        redis.Down = false;
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId),
            new WorkflowTenantWarmMarker { WarmedAt = DateTimeOffset.UtcNow, Bound = [] });
        (await cache.GetTenantWarmStatusAsync(Module, EntityType, TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
        WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId).Should().Be($"qface:qimerp:workflow:warm:{TenantId}");
    }

    [Fact]
    public async Task NothingBound_PairBoundAtWarm_IsUnavailable_WithOperationInMetric()
    {
        var gate = Gate(ModuleAccess.Enabled, new FakeLookupConfigCache(WorkflowConfigLookup.Absent, WorkflowCacheWarmStatus.BoundAtWarm),
            Definitions(WorkflowDefinitionLookupStatus.Absent));
        using var metrics = new GateMetricRecorder();

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType, "UPDATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
        metrics.Unreadable.Should().Contain((WorkflowApprovalGateMetrics.BoundKeyMissing, Module, EntityType, "UPDATE"));
    }

    [Fact]
    public async Task RealServices_PairListedInMarker_KeysMissing_IsUnavailable()
    {
        var redis = new InMemoryGlobalRedis();
        await SeedMarkerAsync(redis, DateTimeOffset.UtcNow, bound: [$"{Module}:{EntityType}"]);

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Fact]
    public async Task RealServices_PairNotListedInMarker_KeysMissing_IsOff()
    {
        var redis = new InMemoryGlobalRedis();
        await SeedMarkerAsync(redis, DateTimeOffset.UtcNow, bound: [$"{Module}:Leave"]);

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Off);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RealServices_MarkerOtherThanVersion2_IsUnavailable(int version)
    {
        var redis = new InMemoryGlobalRedis();
        await SeedMarkerAsync(redis, DateTimeOffset.UtcNow, bound: [], version: version);

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Fact]
    public async Task RealServices_Version1MarkerWithoutBound_IsUnavailable()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId),
            new WorkflowTenantWarmMarker { WarmedAt = DateTimeOffset.UtcNow, Version = 1 });

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE"))
            .Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Theory]
    [InlineData(47, null, WorkflowCacheWarmStatus.Warm)]
    [InlineData(49, null, WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(2, "01:00:00", WorkflowCacheWarmStatus.NotWarm)]
    [InlineData(0, "01:00:00", WorkflowCacheWarmStatus.Warm)]
    public async Task WarmMarker_OlderThanMaxAge_IsNotWarm(int ageHours, string? configuredMaxAge, WorkflowCacheWarmStatus expected)
    {
        var redis = new InMemoryGlobalRedis();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        await SeedMarkerAsync(redis, clock.Now.AddHours(-ageHours), bound: []);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WorkflowTenantWarmMarker.MaxAgeSettingKey] = configuredMaxAge })
            .Build();
        var cache = new WorkflowConfigCacheService(
            SharedCache(redis), new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance, configuration, clock);

        (await cache.GetTenantWarmStatusAsync(Module, EntityType, TenantId)).Should().Be(expected);
    }

    [Fact]
    public async Task WarmMarker_LiteralPlatformJson_IsRead()
    {
        const string json = """{ "warmedAt": "2026-11-14T10:00:00Z", "version": 2, "bound": ["HR:Employee", "GL:Invoice"] }""";
        var marker = Newtonsoft.Json.JsonConvert.DeserializeObject<WorkflowTenantWarmMarker>(json)!;
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId), marker);
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var cache = new WorkflowConfigCacheService(
            SharedCache(redis), new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance, null, clock);

        marker.Version.Should().Be(2);
        marker.WarmedAt.Should().Be(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
        (await cache.GetTenantWarmStatusAsync("HR", "Employee", TenantId)).Should().Be(WorkflowCacheWarmStatus.BoundAtWarm);
        (await cache.GetTenantWarmStatusAsync("HR", "Leave", TenantId)).Should().Be(WorkflowCacheWarmStatus.Warm);
    }

    [Fact]
    public async Task ConfigLookup_AbsentResultIsNotCached()
    {
        var redis = new InMemoryGlobalRedis();
        var cache = ConfigCache(redis);

        (await cache.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status.Should().Be(WorkflowConfigLookupStatus.Absent);
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.Configuration(TenantId, Module, EntityType),
            new EntityWorkflowConfig { EnableWorkflowForCreate = true });
        (await cache.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status.Should().Be(WorkflowConfigLookupStatus.Found);
    }

    [Fact]
    public async Task DefinitionProvider_CachesFoundForScope_AbsentForFiveSeconds_UnknownNever()
    {
        var redis = new InMemoryGlobalRedis();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var provider = new WorkflowDefinitionProvider(SharedCache(redis), NullLogger<WorkflowDefinitionProvider>.Instance, clock);
        var definitionKey = WorkflowDefinitionCacheKeys.PublishedDefinitionByEntityType(TenantId, EntityType);

        (await provider.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Absent);
        await redis.SetAsync(definitionKey, Published());
        (await provider.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Absent);
        clock.Now = clock.Now.AddSeconds(6);
        (await provider.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Found);

        clock.Now = clock.Now.AddMinutes(10);
        redis.Down = true;
        (await provider.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Found);
        (await provider.LookupPublishedDefinitionAsync(TenantId, "WF", EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Unknown);
        redis.Down = false;
        await redis.SetAsync(WorkflowDefinitionCacheKeys.PublishedDefinition(TenantId, "WF", EntityType), Published());
        (await provider.LookupPublishedDefinitionAsync(TenantId, "WF", EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Found);
    }

    [Fact]
    public async Task RequiresApproval_UsesConfiguredWorkflowModule()
    {
        var validation = ValidationService(new Dictionary<string, string?> { ["Workflow:Module"] = Module },
            WorkflowConfigLookup.Found(new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" }));

        (await validation.RequiresApprovalAsync(NewEntity(), "CREATE")).Should().BeTrue();
    }

    [Fact]
    public async Task RequiresApproval_WithoutWorkflowModule_Refuses()
    {
        var validation = ValidationService(new Dictionary<string, string?>(),
            WorkflowConfigLookup.Found(new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" }));

        var act = () => validation.RequiresApprovalAsync(NewEntity(), "CREATE");

        await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
    }

    [Fact]
    public async Task WarmMarker_DefaultImplementation_IsUnknown()
    {
        IWorkflowConfigCacheService legacy = new LegacyConfigCache(null);

        (await legacy.GetTenantWarmStatusAsync(Module, EntityType, TenantId)).Should().Be(WorkflowCacheWarmStatus.Unknown);
    }

    [Fact]
    public async Task DefinitionProvider_FoundAbsentAndUnknown()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.PublishedDefinition(TenantId, "WF", EntityType),
            new PublishedWorkflowDefinition { TenantId = TenantId, WorkflowCode = "WF", EntityType = EntityType });
        var provider = new WorkflowDefinitionProvider(SharedCache(redis), NullLogger<WorkflowDefinitionProvider>.Instance);

        (await provider.LookupPublishedDefinitionAsync(TenantId, "WF", EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Found);
        (await provider.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Absent);
        redis.Down = true;
        var nextScope = new WorkflowDefinitionProvider(SharedCache(redis), NullLogger<WorkflowDefinitionProvider>.Instance);
        (await nextScope.LookupPublishedDefinitionAsync(TenantId, "WF", EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Unknown);
        (await nextScope.LookupPublishedDefinitionByEntityTypeAsync(TenantId, EntityType)).Status.Should().Be(WorkflowDefinitionLookupStatus.Unknown);
        (await nextScope.GetPublishedDefinitionAsync(TenantId, "WF", EntityType)).Should().BeNull();
    }

    [Fact]
    public async Task RealServices_SnapshotPresentWithoutWorkflow_IsOff()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            SharedCacheKeys.TenantModuleSnapshot(Guid.Parse(TenantId)),
            new TenantModuleSnapshotEntry(1, [ModuleKeys.CoreHR]));

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Fact]
    public async Task RealServices_RedisDown_IsUnavailable()
    {
        var redis = new InMemoryGlobalRedis { Down = true };

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ModuleUnknown_ConfigEnablesCreate_ReturnsTrue()
    {
        var service = WorkflowServiceFor(ModuleAccess.Unknown, WorkflowConfigLookup.Found(
            new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" }));

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeTrue();
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown)]
    [InlineData(ModuleAccess.Enabled)]
    public async Task ShouldTriggerWorkflow_NothingBound_MarkerPresent_ReturnsFalse(ModuleAccess access)
    {
        var service = WorkflowServiceFor(access, WorkflowConfigLookup.Absent, Definitions(WorkflowDefinitionLookupStatus.Absent));

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeFalse();
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown, "CREATE")]
    [InlineData(ModuleAccess.Enabled, "CREATE")]
    [InlineData(ModuleAccess.Enabled, "UPDATE")]
    public async Task ShouldTriggerWorkflow_NothingBound_MarkerMissing_Throws(ModuleAccess access, string operation)
    {
        var service = WorkflowServiceFor(access, WorkflowConfigLookup.Absent, Definitions(WorkflowDefinitionLookupStatus.Absent),
            WorkflowCacheWarmStatus.NotWarm);

        var act = () => service.ShouldTriggerWorkflow(NewEntity(), operation, Module);

        await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_PresetCodeUnpublished_EntityTypeDefinitionActive_Refuses()
    {
        var service = WorkflowServiceFor(ModuleAccess.Enabled, WorkflowConfigLookup.Absent,
            new FixedDefinitionProvider(WorkflowDefinitionLookup.Absent, WorkflowDefinitionLookup.Found(Published())));
        var entity = NewEntity();
        entity.WorkflowCode = "X-WF";

        var act = () => service.ShouldTriggerWorkflow(entity, "CREATE", Module);

        await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_PresetCodeActive_ReturnsTrue()
    {
        var service = WorkflowServiceFor(ModuleAccess.Enabled, WorkflowConfigLookup.Absent,
            new FixedDefinitionProvider(WorkflowDefinitionLookup.Found(Published(code: "X-WF")), WorkflowDefinitionLookup.Absent),
            WorkflowCacheWarmStatus.NotWarm);
        var entity = NewEntity();
        entity.WorkflowCode = "X-WF";

        (await service.ShouldTriggerWorkflow(entity, "create", Module)).Should().BeTrue();
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown)]
    [InlineData(ModuleAccess.Enabled)]
    public async Task ShouldTriggerWorkflow_ConfigMissing_PublishedDefinition_CreateReturnsTrue(ModuleAccess access)
    {
        var service = WorkflowServiceFor(access, WorkflowConfigLookup.Absent, Definitions(WorkflowDefinitionLookupStatus.Found));

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeTrue();
    }

    [Theory]
    [InlineData("UPDATE", WorkflowDefinitionLookupStatus.Found)]
    [InlineData("DELETE", WorkflowDefinitionLookupStatus.Found)]
    [InlineData("CREATE", WorkflowDefinitionLookupStatus.Unknown)]
    [InlineData("UPDATE", WorkflowDefinitionLookupStatus.Unknown)]
    public async Task ShouldTriggerWorkflow_ModuleEnabled_ConfigMissing_RefusesWhenDefinitionCannotDecide(
        string operation, WorkflowDefinitionLookupStatus definition)
    {
        var service = WorkflowServiceFor(ModuleAccess.Enabled, WorkflowConfigLookup.Absent, Definitions(definition));

        var act = () => service.ShouldTriggerWorkflow(NewEntity(), operation, Module);

        (await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>()).Which.Operation.Should().Be(operation);
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ConditionEvaluationFailure_Propagates()
    {
        var service = WorkflowServiceFor(ModuleAccess.Enabled, WorkflowConfigLookup.Found(new EntityWorkflowConfig
        {
            EnableWorkflowForCreate = true,
            CreateWorkflowCode = "WF",
            CreateTriggerConditions = [new WorkflowTriggerCondition { Field = nameof(ThrowingWorkflowEntity.Broken), Value = "x" }]
        }));
        var entity = new ThrowingWorkflowEntity { TenantId = TenantId };
        entity.EnableWorkflowProcessing();

        var act = () => service.ShouldTriggerWorkflow(entity, "CREATE", Module);

        (await act.Should().ThrowAsync<TargetInvocationException>())
            .WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ModuleEnabled_ConfigUnreadable_RecordsMetric()
    {
        var service = WorkflowServiceFor(ModuleAccess.Enabled, WorkflowConfigLookup.Unknown);
        using var metrics = new GateMetricRecorder();

        var act = () => service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module);

        await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
        metrics.Unreadable.Should().Contain((WorkflowApprovalGateMetrics.ConfigSource, Module, nameof(TestWorkflowEntity), "CREATE"));
    }

    [Theory]
    [InlineData(ModuleAccess.Unknown)]
    [InlineData(ModuleAccess.Enabled)]
    public async Task ShouldTriggerWorkflow_ConfigUnknown_ThrowsInsteadOfReturningFalse(ModuleAccess access)
    {
        var service = WorkflowServiceFor(access, WorkflowConfigLookup.Unknown);

        var act = () => service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module);

        await act.Should().ThrowAsync<WorkflowApprovalStateUnavailableException>();
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ModuleNotInstalled_ReturnsFalse()
    {
        var service = WorkflowServiceFor(ModuleAccess.NotInstalled, WorkflowConfigLookup.Found(
            new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" }));

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeFalse();
    }

    [Fact]
    public async Task ConfigLookup_FoundAbsentAndUnknown()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.Configuration(TenantId, Module, EntityType),
            new EntityWorkflowConfig { EnableWorkflowForCreate = true });

        var found = await ConfigCache(redis).LookupEntityConfigAsync(Module, EntityType, TenantId);
        var absent = await ConfigCache(redis).LookupEntityConfigAsync(Module, "Other", TenantId);
        redis.Down = true;
        var unknownCache = ConfigCache(redis);
        var unknown = await unknownCache.LookupEntityConfigAsync(Module, EntityType, TenantId);

        found.Status.Should().Be(WorkflowConfigLookupStatus.Found);
        found.Config!.EnableWorkflowForCreate.Should().BeTrue();
        absent.Status.Should().Be(WorkflowConfigLookupStatus.Absent);
        unknown.Status.Should().Be(WorkflowConfigLookupStatus.Unknown);
        (await unknownCache.GetEntityConfigAsync(Module, EntityType, TenantId)).Should().BeNull();
    }

    [Fact]
    public async Task ConfigLookup_UnreadableResultIsNotCached()
    {
        var redis = new InMemoryGlobalRedis { Down = true };
        await redis.SetAsync(
            WorkflowDefinitionCacheKeys.Configuration(TenantId, Module, EntityType),
            new EntityWorkflowConfig { EnableWorkflowForCreate = true });
        var cache = ConfigCache(redis);

        (await cache.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status.Should().Be(WorkflowConfigLookupStatus.Unknown);
        redis.Down = false;
        (await cache.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status.Should().Be(WorkflowConfigLookupStatus.Found);
    }

    [Fact]
    public async Task DefaultInterfaceMembers_MapLegacyImplementations()
    {
        IWorkflowConfigCacheService legacyAbsent = new LegacyConfigCache(null);
        IWorkflowConfigCacheService legacyFound = new LegacyConfigCache(new EntityWorkflowConfig());
        IWorkflowModuleApprovalGate legacyOff = new LegacyGate(enabled: false);
        IWorkflowModuleApprovalGate legacyOn = new LegacyGate(enabled: true);

        (await legacyAbsent.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status
            .Should().Be(WorkflowConfigLookupStatus.Absent);
        (await legacyFound.LookupEntityConfigAsync(Module, EntityType, TenantId)).Status
            .Should().Be(WorkflowConfigLookupStatus.Found);
        (await legacyOff.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.Off);
        (await legacyOn.GetApprovalStateAsync(TenantId, Module, EntityType, "CREATE")).Should().Be(WorkflowApprovalGateState.On);
    }

    private static WorkflowModuleApprovalGate Gate(
        ModuleAccess access,
        IWorkflowConfigCacheService config,
        IWorkflowDefinitionProvider? definitions = null) =>
        new(new ScriptedModuleAccessService(access), config, NullLogger<WorkflowModuleApprovalGate>.Instance, definitions);

    private static IWorkflowDefinitionProvider Definitions(WorkflowDefinitionLookupStatus status)
    {
        var lookup = status switch
        {
            WorkflowDefinitionLookupStatus.Found => WorkflowDefinitionLookup.Found(Published()),
            WorkflowDefinitionLookupStatus.Absent => WorkflowDefinitionLookup.Absent,
            _ => WorkflowDefinitionLookup.Unknown
        };
        return new FixedDefinitionProvider(lookup, lookup);
    }

    private static PublishedWorkflowDefinition Published(bool active = true, string code = "WF") =>
        new() { TenantId = TenantId, WorkflowCode = code, EntityType = EntityType, IsActive = active };

    private static WorkflowModuleApprovalGate RealGate(InMemoryGlobalRedis redis)
    {
        var shared = SharedCache(redis);
        return new WorkflowModuleApprovalGate(
            new TenantModuleAccessService(shared, new HttpContextAccessor()),
            new WorkflowConfigCacheService(shared, new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance),
            NullLogger<WorkflowModuleApprovalGate>.Instance,
            new WorkflowDefinitionProvider(shared, NullLogger<WorkflowDefinitionProvider>.Instance));
    }

    private static Task SeedMarkerAsync(InMemoryGlobalRedis redis, DateTimeOffset warmedAt, List<string> bound, int version = 2) =>
        redis.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(TenantId),
            new WorkflowTenantWarmMarker { WarmedAt = warmedAt, Version = version, Bound = bound });

    private static WorkflowValidationService ValidationService(Dictionary<string, string?> settings, WorkflowConfigLookup lookup)
    {
        var configCache = new FakeLookupConfigCache(lookup);
        return new WorkflowValidationService(
            configCache,
            WorkflowServiceFor(ModuleAccess.Enabled, lookup),
            new FakeCurrentUserService(TenantId),
            NullLogger<WorkflowValidationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WorkflowConfigCacheService ConfigCache(InMemoryGlobalRedis redis) =>
        new(SharedCache(redis), new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

    private static SharedRedisCacheService SharedCache(InMemoryGlobalRedis redis) =>
        new(redis, NullLogger<RedisCacheService>.Instance, new ConfigurationBuilder().Build());

    private static WorkflowService WorkflowServiceFor(
        ModuleAccess access,
        WorkflowConfigLookup lookup,
        IWorkflowDefinitionProvider? definitions = null,
        WorkflowCacheWarmStatus warm = WorkflowCacheWarmStatus.Warm) =>
        new(
            new FakeLookupConfigCache(lookup, warm),
            new ScriptedModuleAccessService(access),
            new TenantContext(),
            Microsoft.Extensions.Options.Options.Create(new SystemOptions()),
            NullLogger<WorkflowService>.Instance,
            new FakeCurrentUserService(TenantId),
            definitions);

    private sealed class ThrowingWorkflowEntity : TestWorkflowEntity
    {
        public string Broken => throw new InvalidOperationException("broken property");
    }

    private sealed class FixedDefinitionProvider(WorkflowDefinitionLookup byCode, WorkflowDefinitionLookup byEntity)
        : IWorkflowDefinitionProvider
    {
        public Task<PublishedWorkflowDefinition?> GetPublishedDefinitionAsync(
            string tenantId, string workflowCode, string entityType, CancellationToken cancellationToken = default) =>
            Task.FromResult(byCode.Definition);

        public Task<PublishedWorkflowDefinition?> GetPublishedDefinitionByEntityTypeAsync(
            string tenantId, string entityType, CancellationToken cancellationToken = default) =>
            Task.FromResult(byEntity.Definition);

        public Task<WorkflowDefinitionLookup> LookupPublishedDefinitionAsync(
            string tenantId, string workflowCode, string entityType, CancellationToken cancellationToken = default) =>
            Task.FromResult(byCode);

        public Task<WorkflowDefinitionLookup> LookupPublishedDefinitionByEntityTypeAsync(
            string tenantId, string entityType, CancellationToken cancellationToken = default) =>
            Task.FromResult(byEntity);
    }

    private static TestWorkflowEntity NewEntity()
    {
        var entity = new TestWorkflowEntity { Title = "New hire" };
        entity.EnableWorkflowProcessing();
        entity.TenantId = TenantId;
        return entity;
    }

    private sealed class FakeLookupConfigCache(
        WorkflowConfigLookup lookup,
        WorkflowCacheWarmStatus warm = WorkflowCacheWarmStatus.Warm) : IWorkflowConfigCacheService
    {
        public int LookupCount { get; private set; }

        public Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(warm);

        public Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null)
        {
            LookupCount++;
            return Task.FromResult(lookup);
        }

        public Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(lookup.Config);

        public Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(lookup.Config is { } config && operation.ToUpperInvariant() switch
            {
                "CREATE" => config.EnableWorkflowForCreate,
                "UPDATE" => config.EnableWorkflowForUpdate,
                "DELETE" => config.EnableWorkflowForDelete,
                _ => false
            });

        public Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(lookup.Config?.CreateWorkflowCode);

        public Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
            string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(lookup.Config?.CreateTriggerConditions ?? []);
    }

    private sealed class LegacyConfigCache(EntityWorkflowConfig? config) : IWorkflowConfigCacheService
    {
        public Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
            Task.FromResult(config);

        public Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(false);

        public Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult<string?>(null);

        public Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
            string module, string entityType, string operation, string? tenantId = null) =>
            Task.FromResult(new List<WorkflowTriggerCondition>());
    }

    private sealed class LegacyGate(bool enabled) : IWorkflowModuleApprovalGate
    {
        public Task<bool> IsApprovalModuleEnabledAsync(string? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(enabled);
    }

    private sealed class GateMetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Outcome, string Module, string EntityType)> _outcomes = [];
        private readonly List<(string Source, string Module, string EntityType, string Operation)> _unreadable = [];

        public GateMetricRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == WorkflowApprovalGateMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var values = new Dictionary<string, string>();
                foreach (var tag in tags)
                    values[tag.Key] = tag.Value?.ToString() ?? string.Empty;

                lock (_outcomes)
                {
                    if (instrument.Name == WorkflowApprovalGateMetrics.StateUnreadableInstrument)
                        _unreadable.Add((values.GetValueOrDefault("source", ""), values.GetValueOrDefault("module", ""),
                            values.GetValueOrDefault("entity_type", ""), values.GetValueOrDefault("operation", "")));
                    else
                        _outcomes.Add((values.GetValueOrDefault("outcome", ""), values.GetValueOrDefault("module", ""),
                            values.GetValueOrDefault("entity_type", "")));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(string Outcome, string Module, string EntityType)> Outcomes
        {
            get
            {
                lock (_outcomes)
                    return _outcomes.ToList();
            }
        }

        public IReadOnlyList<(string Source, string Module, string EntityType, string Operation)> Unreadable
        {
            get
            {
                lock (_outcomes)
                    return _unreadable.ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class InMemoryGlobalRedis : IGlobalRedisCacheService
{
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    public bool Down { get; set; }

    public Task<T> GetAsync<T>(string key)
    {
        ThrowIfDown();
        return Task.FromResult(_store.TryGetValue(key, out var value) ? (T)value! : default!);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
    {
        ThrowIfDown();
        return Task.FromResult(_store.ContainsKey(key));
    }

    public Task<bool> RemoveAsync(string key) => Task.FromResult(_store.Remove(key));
    public Task<Dictionary<string, T>> GetManyAsync<T>(params string[] keys) => throw new NotSupportedException();
    public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task RemoveManyAsync(params string[] keys) => throw new NotSupportedException();
    public Task<long> RemoveByPatternAsync(string pattern) => throw new NotSupportedException();
    public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null) => throw new NotSupportedException();
    public Task<TimeSpan?> GetTimeToLiveAsync(string key) => throw new NotSupportedException();
    public Task<bool> ExtendExpirationAsync(string key, TimeSpan expiration) => throw new NotSupportedException();
    public Task<T> HashGetAsync<T>(string key, string field) => throw new NotSupportedException();
    public Task<bool> HashSetAsync<T>(string key, string field, T value) => throw new NotSupportedException();
    public Task<Dictionary<string, T>> HashGetAllAsync<T>(string key) => throw new NotSupportedException();
    public Task<long> ListPushAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<T> ListPopAsync<T>(string key) => throw new NotSupportedException();
    public Task<List<T>> ListGetRangeAsync<T>(string key, long start, long stop) => throw new NotSupportedException();
    public Task<bool> SetAddAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<bool> SetContainsAsync<T>(string key, T value) => throw new NotSupportedException();
    public Task<List<T>> SetGetAllAsync<T>(string key) => throw new NotSupportedException();

    private void ThrowIfDown()
    {
        if (Down)
            throw new TimeoutException("redis unavailable");
    }
}
