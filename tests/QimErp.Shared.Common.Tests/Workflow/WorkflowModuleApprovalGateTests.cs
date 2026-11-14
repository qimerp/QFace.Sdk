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

    [Theory]
    [InlineData(ModuleAccess.Enabled, WorkflowApprovalGateState.On)]
    [InlineData(ModuleAccess.NotInstalled, WorkflowApprovalGateState.Off)]
    public async Task KnownModuleState_DecidesWithoutReadingConfig(ModuleAccess access, WorkflowApprovalGateState expected)
    {
        var config = new FakeLookupConfigCache(WorkflowConfigLookup.Unknown);
        var gate = Gate(access, config);

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(expected);
        config.LookupCount.Should().Be(0);
    }

    [Fact]
    public async Task ModuleUnknown_ConfigFound_TurnsApprovalOn()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(
            WorkflowConfigLookup.Found(new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" })));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.On);
    }

    [Fact]
    public async Task ModuleUnknown_ConfigAbsent_TurnsApprovalOff()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Absent));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Fact]
    public async Task ModuleUnknown_ConfigUnknown_IsUnavailable()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Unknown));

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ModuleUnknown_NoWorkflowModuleName_IsUnavailable(string? module)
    {
        var config = new FakeLookupConfigCache(WorkflowConfigLookup.Absent);
        var gate = Gate(ModuleAccess.Unknown, config);

        (await gate.GetApprovalStateAsync(TenantId, module, EntityType)).Should().Be(WorkflowApprovalGateState.Unavailable);
        config.LookupCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyTenant_IsOff()
    {
        var gate = Gate(ModuleAccess.Unknown, new FakeLookupConfigCache(WorkflowConfigLookup.Unknown));

        (await gate.GetApprovalStateAsync(" ", Module, EntityType)).Should().Be(WorkflowApprovalGateState.Off);
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
    [InlineData(WorkflowConfigLookupStatus.Absent, WorkflowApprovalGateMetrics.ConfigAbsent)]
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

        await gate.GetApprovalStateAsync(TenantId, Module, EntityType);

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

        (await gate.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.On);
    }

    [Fact]
    public async Task RealServices_SnapshotPresentWithoutWorkflow_IsOff()
    {
        var redis = new InMemoryGlobalRedis();
        await redis.SetAsync(
            SharedCacheKeys.TenantModuleSnapshot(Guid.Parse(TenantId)),
            new TenantModuleSnapshotEntry(1, [ModuleKeys.CoreHR]));

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.Off);
    }

    [Fact]
    public async Task RealServices_RedisDown_IsUnavailable()
    {
        var redis = new InMemoryGlobalRedis { Down = true };

        (await RealGate(redis).GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.Unavailable);
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ModuleUnknown_ConfigEnablesCreate_ReturnsTrue()
    {
        var service = WorkflowServiceFor(ModuleAccess.Unknown, WorkflowConfigLookup.Found(
            new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "WF" }));

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldTriggerWorkflow_ModuleUnknown_ConfigAbsent_ReturnsFalse()
    {
        var service = WorkflowServiceFor(ModuleAccess.Unknown, WorkflowConfigLookup.Absent);

        (await service.ShouldTriggerWorkflow(NewEntity(), "CREATE", Module)).Should().BeFalse();
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
        (await legacyOff.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.Off);
        (await legacyOn.GetApprovalStateAsync(TenantId, Module, EntityType)).Should().Be(WorkflowApprovalGateState.On);
    }

    private static WorkflowModuleApprovalGate Gate(ModuleAccess access, IWorkflowConfigCacheService config) =>
        new(new ScriptedModuleAccessService(access), config, NullLogger<WorkflowModuleApprovalGate>.Instance);

    private static WorkflowModuleApprovalGate RealGate(InMemoryGlobalRedis redis)
    {
        var shared = SharedCache(redis);
        return new WorkflowModuleApprovalGate(
            new TenantModuleAccessService(shared, new HttpContextAccessor()),
            new WorkflowConfigCacheService(shared, new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance),
            NullLogger<WorkflowModuleApprovalGate>.Instance);
    }

    private static WorkflowConfigCacheService ConfigCache(InMemoryGlobalRedis redis) =>
        new(SharedCache(redis), new TenantContext(), NullLogger<WorkflowConfigCacheService>.Instance);

    private static SharedRedisCacheService SharedCache(InMemoryGlobalRedis redis) =>
        new(redis, NullLogger<RedisCacheService>.Instance, new ConfigurationBuilder().Build());

    private static WorkflowService WorkflowServiceFor(ModuleAccess access, WorkflowConfigLookup lookup) =>
        new(
            new FakeLookupConfigCache(lookup),
            new ScriptedModuleAccessService(access),
            new TenantContext(),
            Microsoft.Extensions.Options.Options.Create(new SystemOptions()),
            NullLogger<WorkflowService>.Instance,
            new FakeCurrentUserService(TenantId));

    private static TestWorkflowEntity NewEntity()
    {
        var entity = new TestWorkflowEntity { Title = "New hire" };
        entity.EnableWorkflowProcessing();
        entity.TenantId = TenantId;
        return entity;
    }

    private sealed class FakeLookupConfigCache(WorkflowConfigLookup lookup) : IWorkflowConfigCacheService
    {
        public int LookupCount { get; private set; }

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

        public GateMetricRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == WorkflowApprovalGateMetrics.MeterName
                    && instrument.Name == WorkflowApprovalGateMetrics.ModuleStateUnknownInstrument)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var values = new Dictionary<string, string>();
                foreach (var tag in tags)
                    values[tag.Key] = tag.Value?.ToString() ?? string.Empty;

                lock (_outcomes)
                    _outcomes.Add((values.GetValueOrDefault("outcome", ""), values.GetValueOrDefault("module", ""), values.GetValueOrDefault("entity_type", "")));
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
