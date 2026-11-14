using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.Workflow;

namespace QimErp.Shared.Common.Testing;

/// <summary>
/// Test-suite support for approval gating, which refuses saves with nothing bound unless the tenant warm marker is present.
/// </summary>
public static class WorkflowCacheTestSupport
{
    /// <remarks>Seed through the shared (unprefixed) cache the SDK reads; a disabled or no-op cache drops the write, so use <see cref="WarmTenantsWorkflowConfigCache"/> there.</remarks>
    public static Task SeedTenantWarmMarkerAsync(IDistributedCacheService cache, string tenantId) =>
        cache.SetAsync(
            WorkflowDefinitionCacheKeys.TenantWarmMarker(tenantId),
            new WorkflowTenantWarmMarker { WarmedAt = DateTimeOffset.UtcNow },
            TimeSpan.FromDays(365));

    /// <summary>Wraps the registered <see cref="IWorkflowConfigCacheService"/> so the given tenants report a warm cache.</summary>
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

/// <summary>Decorator that reports <see cref="WorkflowCacheWarmStatus.Warm"/> for the given tenants and forwards everything else.</summary>
public sealed class WarmTenantsWorkflowConfigCache(IWorkflowConfigCacheService inner, IEnumerable<string> warmTenantIds)
    : IWorkflowConfigCacheService
{
    private readonly HashSet<string> _warmTenantIds = new(warmTenantIds, StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowCacheWarmStatus> GetTenantWarmStatusAsync(string? tenantId = null) =>
        tenantId is not null && _warmTenantIds.Contains(tenantId)
            ? Task.FromResult(WorkflowCacheWarmStatus.Warm)
            : inner.GetTenantWarmStatusAsync(tenantId);

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
