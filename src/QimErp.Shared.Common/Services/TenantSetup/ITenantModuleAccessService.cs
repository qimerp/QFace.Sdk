namespace QimErp.Shared.Common.Services.TenantSetup;

public interface ITenantModuleAccessService
{
    /// <summary>
    /// HTTP module gating only. An unknown module state reports false, which fails OPEN for approvals;
    /// approval decisions must use <c>IWorkflowModuleApprovalGate.GetApprovalStateAsync</c>.
    /// </summary>
    Task<bool> IsModuleEnabledAsync(string? tenantId, string moduleKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>?> GetInstalledModuleKeysAsync(
        string? tenantId,
        CancellationToken cancellationToken = default);

    /// <remarks>The default implementation cannot report <see cref="ModuleAccess.Unknown"/>; override it.</remarks>
    async Task<ModuleAccess> GetModuleAccessAsync(
        string? tenantId,
        string moduleKey,
        CancellationToken cancellationToken = default) =>
        await IsModuleEnabledAsync(tenantId, moduleKey, cancellationToken)
            ? ModuleAccess.Enabled
            : ModuleAccess.NotInstalled;
}
