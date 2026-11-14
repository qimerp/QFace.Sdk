using Microsoft.Extensions.DependencyInjection;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;

namespace QimErp.Shared.Common.Testing;

public static class WorkflowCacheTestSupport
{
    public static Task SeedTenantWarmMarkerAsync(
        ISharedDistributedCacheService cache,
        string tenantId,
        IEnumerable<string>? boundPairs = null) =>
        cache.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(tenantId),
            new WorkflowTenantWarmMarker
            {
                WarmedAt = DateTimeOffset.UtcNow,
                Version = WorkflowTenantWarmMarker.CurrentVersion,
                Bound = boundPairs?.ToList() ?? []
            },
            WorkflowTenantWarmMarker.DefaultMaxAge);

    public static IServiceCollection AddWarmWorkflowTenants(this IServiceCollection services, params string[] tenantIds)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IWorkflowConfigCacheService))
            ?? throw new InvalidOperationException(
                $"{nameof(IWorkflowConfigCacheService)} is not registered; register it before calling {nameof(AddWarmWorkflowTenants)}.");

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(IWorkflowConfigCacheService),
            sp => new WarmTenantsWorkflowConfigCache(CreateInner(sp, existing), tenantIds),
            existing.Lifetime));
        return services;
    }

    private static IWorkflowConfigCacheService CreateInner(IServiceProvider sp, ServiceDescriptor existing) =>
        (IWorkflowConfigCacheService)(existing.ImplementationInstance
            ?? existing.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!));
}

/// <summary>Reports a warm cache for the given tenants (pairs in <paramref name="boundPairs"/> stay bound) and forwards everything else.</summary>
public sealed class WarmTenantsWorkflowConfigCache(
    IWorkflowConfigCacheService inner,
    IEnumerable<string> warmTenantIds,
    IEnumerable<string>? boundPairs = null) : IWorkflowConfigCacheService
{
    private readonly HashSet<string> _warmTenantIds = new(warmTenantIds, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _boundPairs = new(boundPairs ?? [], StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string module, string entityType, string? tenantId = null)
    {
        if (tenantId is null || !_warmTenantIds.Contains(tenantId))
            return inner.GetTenantWarmStatusAsync(module, entityType, tenantId);

        var marker = new WorkflowTenantWarmMarker { Version = WorkflowTenantWarmMarker.CurrentVersion, Bound = [.. _boundPairs] };
        return Task.FromResult(marker.IsBound(module, entityType)
            ? WorkflowCacheWarmStatus.BoundAtWarm
            : WorkflowCacheWarmStatus.Warm);
    }

    public Task<WorkflowConfigLookup> LookupEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        inner.LookupEntityConfigAsync(module, entityType, tenantId);

#pragma warning disable CS0618
    public Task<EntityWorkflowConfig?> GetEntityConfigAsync(string module, string entityType, string? tenantId = null) =>
        inner.GetEntityConfigAsync(module, entityType, tenantId);

    public Task<bool> IsWorkflowEnabledAsync(string module, string entityType, string operation, string? tenantId = null) =>
        inner.IsWorkflowEnabledAsync(module, entityType, operation, tenantId);

    public Task<string?> GetWorkflowCodeAsync(string module, string entityType, string operation, string? tenantId = null) =>
        inner.GetWorkflowCodeAsync(module, entityType, operation, tenantId);

    public Task<List<WorkflowTriggerCondition>> GetTriggerConditionsAsync(
        string module,
        string entityType,
        string operation,
        string? tenantId = null) =>
        inner.GetTriggerConditionsAsync(module, entityType, operation, tenantId);
#pragma warning restore CS0618
}
