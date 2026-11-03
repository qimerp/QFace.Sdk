using System.Net.Http.Json;
using System.Globalization;
using QimErp.Shared.Common.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Options;
using QimErp.Shared.Common.Services.Auth;

namespace QimErp.Shared.Common.Services.Employees;

public sealed class EmployeeSyncProjectionClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<InternalApiOptions> internalApiOptions,
    ICurrentUserService currentUserService,
    ILogger<EmployeeSyncProjectionClient> logger,
    TimeProvider timeProvider) : IEmployeeSyncProjectionClient
{
    public const string HttpClientName = nameof(EmployeeSyncProjectionClient);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<EmployeeSyncProjection?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
        => await GetProjectionAsync(employeeId, "sync-projection", cancellationToken);

    public async Task<string?> GetDeletionEmailAsync(Guid employeeId, CancellationToken cancellationToken = default)
        => (await GetProjectionAsync(employeeId, "deletion-identity", cancellationToken))?.Email;

    private async Task<EmployeeSyncProjection?> GetProjectionAsync(Guid employeeId, string resource, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["People:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogError("People:BaseUrl is not configured — cannot load employee sync projection {EmployeeId}", employeeId);
            return null;
        }

        var apiKey = internalApiOptions.Value.ExpectedApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            logger.LogError("InternalApi:ExpectedApiKey is not configured — cannot call People sync projection");
            return null;
        }

        var tenantId = currentUserService.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogError("TenantId is not set — cannot call People sync projection for {EmployeeId}", employeeId);
            return null;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var tenant = tenantId.Trim();
        var path = $"/internal/employees/{employeeId:D}/{resource}";
        var timestamp = timeProvider.GetUtcNow();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        request.Headers.Add("X-Internal-Api-Key", apiKey);
        request.Headers.Add(InternalServiceSignature.TenantHeader, tenant);
        request.Headers.Add(InternalServiceSignature.TimestampHeader, timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(InternalServiceSignature.SignatureHeader,
            InternalServiceSignature.Sign(apiKey, tenant, request.Method.Method, path, timestamp));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EmployeeSyncProjection>(JsonOptions, cancellationToken);
    }
}
