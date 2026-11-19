using System.Globalization;
using Microsoft.Extensions.Options;
using QimErp.Shared.Common.Constants;
using QimErp.Shared.Common.Filters;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Services.MultiTenancy;

namespace QimErp.Shared.Common.Integrations.Microsoft365;

public sealed class Microsoft365ConnectionResolver(
    ISharedDistributedCacheService cache,
    IHttpClientFactory httpClientFactory,
    IOptions<Microsoft365CatalogOptions> catalogOptions,
    TimeProvider timeProvider,
    ILogger<Microsoft365ConnectionResolver> logger)
{
    public async Task<Microsoft365ConnectionResolution> ResolveAsync(
        string tenantId,
        string companyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || companyId is null)
            return Unavailable();
        if (!string.IsNullOrWhiteSpace(companyId) && !Guid.TryParse(companyId, out _))
            return Invalid(string.Empty, string.Empty);

        var catalog = await ReadCatalogAsync(tenantId);
        if (catalog is null)
        {
            if (!await HydrateAsync(tenantId, cancellationToken))
                return Unavailable();
            catalog = await ReadCatalogAsync(tenantId);
        }
        if (catalog is null)
            return Unavailable();
        if (string.IsNullOrWhiteSpace(companyId) && catalog.CompanyConnections.Count > 0)
            return Invalid(string.Empty, catalog.Revision);

        var normalizedCompany = CompanyIdNormalizer.Normalize(companyId);
        var sourceCompany = string.Empty;
        var connection = catalog.DefaultConnection;
        if (normalizedCompany.Length > 0 && catalog.CompanyConnections.TryGetValue(normalizedCompany, out var companyConnection))
        {
            sourceCompany = normalizedCompany;
            connection = companyConnection;
            if (connection is null)
                return Invalid(sourceCompany, catalog.Revision);
        }
        if (connection is null)
            return new Microsoft365ConnectionResolution
            {
                Status = Microsoft365ConnectionStatus.NotConfigured,
                Revision = catalog.Revision
            };
        if (!string.IsNullOrWhiteSpace(connection.Error) || !HasCredentials(connection.Options))
            return Invalid(sourceCompany, catalog.Revision);

        return new Microsoft365ConnectionResolution
        {
            Status = Microsoft365ConnectionStatus.Ready,
            Options = connection.Options,
            SourceCompanyId = sourceCompany,
            Revision = catalog.Revision
        };
    }

    private async Task<Microsoft365ConnectionCatalog?> ReadCatalogAsync(string tenantId)
    {
        try
        {
            var payload = await cache.GetAsync<string>(SharedCacheKeys.Microsoft365ConnectionCatalog(tenantId), null);
            if (string.IsNullOrWhiteSpace(payload))
                return null;
            var catalog = Microsoft365CatalogProtection.Unprotect(payload, tenantId, catalogOptions.Value.ProtectionKey);
            return catalog.IsComplete && !string.IsNullOrWhiteSpace(catalog.Revision)
                   && catalog.ValidUntilUtc > timeProvider.GetUtcNow() && catalog.CompanyConnections is not null
                   && catalog.CompanyConnections.Keys.All(company => Guid.TryParse(company, out var id)
                       && string.Equals(company, id.ToString("D"), StringComparison.Ordinal))
                ? catalog
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Microsoft 365 connection catalog could not be verified for tenant {TenantId}", tenantId);
            return null;
        }
    }

    private async Task<bool> HydrateAsync(string tenantId, CancellationToken cancellationToken)
    {
        var options = catalogOptions.Value;
        if (string.IsNullOrWhiteSpace(options.HydrationKey)
            || !Uri.TryCreate(options.PlatformOrigin, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && !(origin.IsLoopback && origin.Scheme == Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;

        var timestamp = timeProvider.GetUtcNow();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(origin, Microsoft365CatalogOptions.HydrationPath));
        request.Headers.Add(InternalServiceSignature.TenantHeader, tenantId);
        request.Headers.Add(InternalServiceSignature.TimestampHeader,
            timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(InternalServiceSignature.SignatureHeader,
            InternalServiceSignature.Sign(options.HydrationKey, tenantId, "POST", Microsoft365CatalogOptions.HydrationPath, timestamp));
        try
        {
            using var client = httpClientFactory.CreateClient("microsoft365-catalog");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Microsoft 365 catalog hydration was unavailable for tenant {TenantId}", tenantId);
            return false;
        }
    }

    private static bool HasCredentials(TenantMicrosoft365ResolvedOptions? options) =>
        options is not null && !string.IsNullOrWhiteSpace(options.EntraTenantId)
                            && !string.IsNullOrWhiteSpace(options.ClientId)
                            && !string.IsNullOrWhiteSpace(options.ClientSecret);

    private static Microsoft365ConnectionResolution Unavailable() => new()
    {
        Status = Microsoft365ConnectionStatus.Unavailable,
        Error = "Microsoft 365 connection settings could not be verified."
    };

    private static Microsoft365ConnectionResolution Invalid(string companyId, string revision) => new()
    {
        Status = Microsoft365ConnectionStatus.Invalid,
        SourceCompanyId = companyId,
        Revision = revision,
        Error = "The selected Microsoft 365 connection is invalid."
    };
}
