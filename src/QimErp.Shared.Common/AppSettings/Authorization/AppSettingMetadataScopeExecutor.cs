using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.MultiTenancy;

namespace QimErp.Shared.Common.AppSettings.Authorization;

public enum AppSettingMetadataOperation { Read, Write }

public sealed record AppSettingMetadataAuthorization(
    string ActorId, string TenantId, IReadOnlyCollection<string> ReadCompanyIds);

public interface IAppSettingMetadataAuthorizer
{
    Task<AppSettingMetadataAuthorization> AuthorizeAsync(string policyName,
        AppSettingMetadataOperation operation, string? targetCompanyId, CancellationToken cancellationToken);
}

public interface IAppSettingMetadataQueryContext : IScopedQueryFilterContext
{
    string MetadataTenantId { get; }
    string MetadataCategory { get; }
    string[] MetadataKeys { get; }
    string[] MetadataReadCompanyIds { get; }
    bool MetadataReadAllCompanies { get; }
}

public static class AppSettingMetadataPolicy
{
    public const string Microsoft365OwnerManagement = "Microsoft365OwnerManagement";
    public const string IdentityOwnerManagement = "IdentityOwnerManagement";
    internal const string Category = "Microsoft365Integration";
    internal static readonly string[] Keys = ["microsoft365.graph_config", "microsoft365.company_graph_config",
        "microsoft365.sign_in_binding", "microsoft365.company_sign_in_binding"];
}

public sealed class AppSettingMetadataScopeExecutor(IAppSettingMetadataAuthorizer authorizer, ITenantContext tenantContext, ICurrentUserService currentUser, TimeProvider? timeProvider = null)
{
    private sealed record Capability(string PolicyName, string ActorId, string TenantId, string WriteCompanyId,
        string[] ReadCompanyIds, AppSettingMetadataOperation Operation, DateTimeOffset Expires,
        ITenantContext TenantContext, ICurrentUserService User, TimeProvider Clock)
    {
        public int Active = 1;
    }
    private static readonly AsyncLocal<Capability?> Ambient = new();
    private static Capability? Current
    {
        get
        {
            var capability = Ambient.Value;
            if (capability is not null && (Volatile.Read(ref capability.Active) == 0
                || capability.Expires <= capability.Clock.GetUtcNow() || capability.TenantContext.TenantId != capability.TenantId
                || capability.User.GetTenantId() != capability.TenantId || capability.User.GetUserId() != capability.ActorId
                || !capability.User.IsAuthenticated))
                throw new UnauthorizedAccessException("The settings metadata operation expired.");
            return capability;
        }
    }
    public static string CurrentActorId => Current?.ActorId ?? string.Empty;
    public static bool IsAuthorizedTarget(string tenantId, string? targetCompanyId, bool requireWrite = false) =>
        Current is { PolicyName: AppSettingMetadataPolicy.Microsoft365OwnerManagement } c && c.TenantId == tenantId && c.WriteCompanyId == (targetCompanyId ?? string.Empty)
        && (!requireWrite || c.Operation == AppSettingMetadataOperation.Write);
    public static bool CanAuthorizeSessionRevocation(string tenantId, string? targetCompanyId) =>
        Current is { Operation: AppSettingMetadataOperation.Write } c
        && (c.PolicyName is AppSettingMetadataPolicy.Microsoft365OwnerManagement or AppSettingMetadataPolicy.IdentityOwnerManagement)
        && c.TenantId == tenantId
        && c.WriteCompanyId == (targetCompanyId ?? string.Empty);
    public static string CurrentTenantId => Current?.TenantId ?? string.Empty;
    public static string CurrentCategory => Current is { PolicyName: AppSettingMetadataPolicy.Microsoft365OwnerManagement } ? AppSettingMetadataPolicy.Category : string.Empty;
    public static string[] CurrentKeys => Current is { PolicyName: AppSettingMetadataPolicy.Microsoft365OwnerManagement } ? AppSettingMetadataPolicy.Keys.ToArray() : [];
    public static bool CurrentReadAllCompanies => Current is { PolicyName: AppSettingMetadataPolicy.Microsoft365OwnerManagement };
    public static string[] CurrentReadCompanyIds => Current is { PolicyName: AppSettingMetadataPolicy.Microsoft365OwnerManagement } c ? c.ReadCompanyIds.ToArray() : [];

    public async Task<T> RunAsync<T>(string policyName, AppSettingMetadataOperation operation,
        string? targetCompanyId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (Ambient.Value is not null) throw new UnauthorizedAccessException("Nested metadata operations are not allowed.");
        if (policyName != AppSettingMetadataPolicy.Microsoft365OwnerManagement && policyName != AppSettingMetadataPolicy.IdentityOwnerManagement)
            throw new UnauthorizedAccessException("Unknown settings metadata policy.");
        if (!Enum.IsDefined(operation) || policyName == AppSettingMetadataPolicy.IdentityOwnerManagement && operation != AppSettingMetadataOperation.Write)
            throw new UnauthorizedAccessException("Unknown metadata operation.");
        var target = targetCompanyId ?? string.Empty;
        if (target.Length > 0 && (!Guid.TryParse(target, out var id) || id.ToString() != target))
            throw new UnauthorizedAccessException("Invalid settings metadata target.");
        cancellationToken.ThrowIfCancellationRequested();
        var grant = await authorizer.AuthorizeAsync(policyName, operation, targetCompanyId, cancellationToken);
        if (!Guid.TryParse(grant.TenantId, out var tenantId) || tenantId == Guid.Empty
            || tenantId.ToString() != grant.TenantId || grant.TenantId != tenantContext.TenantId
            || grant.TenantId != currentUser.GetTenantId() || !currentUser.IsAuthenticated
            || string.IsNullOrWhiteSpace(grant.ActorId) || grant.ActorId != currentUser.GetUserId()
            || grant.ReadCompanyIds is null
            || grant.ReadCompanyIds.Any(id => id is null || (id.Length > 0
                && (!Guid.TryParse(id, out var company) || company == Guid.Empty || company.ToString() != id))))
            throw new UnauthorizedAccessException("Invalid settings metadata authority.");
        var companies = grant.ReadCompanyIds.Append(string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (!companies.Contains(target, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The settings target is outside the authorized tenant.");
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Ambient.Value;
        var capability = new Capability(policyName, grant.ActorId, grant.TenantId, target, companies, operation, (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(30), tenantContext, currentUser, timeProvider ?? TimeProvider.System);
        Ambient.Value = capability;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await action(deadline.Token); }
        finally { Interlocked.Exchange(ref capability.Active, 0); Ambient.Value = previous; }
    }

    internal static bool CanWrite(string tenantId, string companyId, string key, string category,
        AppSettingScope scope, bool isGlobal)
    {
        var c = Current;
        if (c is null || c.PolicyName != AppSettingMetadataPolicy.Microsoft365OwnerManagement || c.Operation != AppSettingMetadataOperation.Write || isGlobal
            || tenantId != c.TenantId || companyId != c.WriteCompanyId
            || category != AppSettingMetadataPolicy.Category || !AppSettingMetadataPolicy.Keys.Contains(key, StringComparer.Ordinal))
            return false;
        return companyId.Length == 0
            ? (key is "microsoft365.graph_config" or "microsoft365.sign_in_binding") && scope == AppSettingScope.TenantOnly
            : (key is "microsoft365.company_graph_config" or "microsoft365.company_sign_in_binding") && scope == AppSettingScope.CompanyOverridable;
    }
}

public interface IIdentitySessionRevocationTarget
{
    string UserId { get; }
    bool IsActive { get; }
}

public interface IIdentitySessionRevocationWriteContext
{
    string RevocationTenantId { get; }
    string RevocationActorId { get; }
    string? RevocationTargetCompanyId { get; }
    string[] RevocationSessionIds { get; }
}
