using Microsoft.AspNetCore.Http;
using QimErp.Shared.Common.Constants;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.TenantSetup;

namespace QimErp.Shared.Common.Services.TenantSetup;

public sealed class TenantModuleAccessService(
    ISharedDistributedCacheService cache,
    IHttpContextAccessor httpContextAccessor) : ITenantModuleAccessService
{
    private static string SnapshotItemKey(Guid tenantId) => $"tenant-module-snapshot:{tenantId}";
    public async Task<bool> IsModuleEnabledAsync(
        string? tenantId,
        string moduleKey,
        CancellationToken cancellationToken = default) =>
        await GetModuleAccessAsync(tenantId, moduleKey, cancellationToken) == ModuleAccess.Enabled;

    public async Task<ModuleAccess> GetModuleAccessAsync(
        string? tenantId,
        string moduleKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return ModuleAccess.NotInstalled;
        }

        // An empty moduleKey means "no module required", not "unknown tenant".
        if (string.IsNullOrWhiteSpace(moduleKey))
        {
            return ModuleAccess.Enabled;
        }

        if (BaseModel.IncludedModuleKeys.Contains(moduleKey, StringComparer.OrdinalIgnoreCase))
        {
            return ModuleAccess.Enabled;
        }

        var snapshot = await ReadSnapshotAsync(tenantId);
        if (snapshot is null)
        {
            return ModuleAccess.Unknown;
        }

        return ModuleGuard.IsExplicitlySelected(snapshot.InstalledModuleKeys, moduleKey)
            ? ModuleAccess.Enabled
            : ModuleAccess.NotInstalled;
    }

    public async Task<IReadOnlyList<string>?> GetInstalledModuleKeysAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var snapshot = await ReadSnapshotAsync(tenantId);
        return snapshot?.InstalledModuleKeys;
    }

    private async Task<TenantModuleSnapshotEntry?> ReadSnapshotAsync(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out var tenantGuid))
        {
            return null;
        }

        var itemKey = SnapshotItemKey(tenantGuid);
        if (httpContextAccessor.HttpContext?.Items.TryGetValue(itemKey, out var cached) == true
            && cached is TenantModuleSnapshotEntry cachedSnapshot)
        {
            return cachedSnapshot;
        }

        var read = await cache.ReadEntryAsync<TenantModuleSnapshotEntry>(
            SharedCacheKeys.TenantModuleSnapshot(tenantGuid));
        if (read.Status != CacheReadStatus.Hit || read.Value is null)
        {
            return null;
        }

        if (httpContextAccessor.HttpContext is not null)
        {
            httpContextAccessor.HttpContext.Items[itemKey] = read.Value;
        }

        return read.Value;
    }
}
