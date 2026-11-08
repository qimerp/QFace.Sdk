using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using QimErp.Shared.Common.Filters;

namespace QimErp.Shared.Common.Authorization;

internal sealed class AccessTokenAuthority(Uri origin, string key)
{
    internal const string ClientName = "PlatformAccessTokenAuthority";

    internal static AccessTokenAuthority? Configure(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("AccessTokenValidation:RemoteEnabled"))
            return null;

        var address = configuration["AccessTokenValidation:PlatformOrigin"];
        var key = configuration["InternalApi:ExpectedApiKey"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath != "/" || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Remote access-token validation requires a Platform HTTPS origin and internal API key.");

        services.AddHttpClient(ClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return new AccessTokenAuthority(origin, key);
    }

    internal async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var generation = principal?.FindFirst("access_generation")?.Value;
        if (!Guid.TryParse(principal?.FindFirst("tenantId")?.Value, out var tenant) || tenant == Guid.Empty
            || !Guid.TryParse(principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal?.FindFirst("sub")?.Value, out var subject)
            || subject == Guid.Empty || generation is not { Length: 64 })
        {
            context.Fail("Invalid access token identity or generation.");
            return;
        }

        var tenantId = tenant.ToString("D");
        var path = $"/internal/auth/access-generation/{subject:D}";
        var now = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path))
        {
            Content = JsonContent.Create(new { generation })
        };
        request.Headers.Add(InternalServiceSignature.TenantHeader, tenantId);
        request.Headers.Add(InternalServiceSignature.TimestampHeader, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(InternalServiceSignature.SignatureHeader,
            InternalServiceSignature.Sign(key, tenantId, "POST", path, now));

        try
        {
            var factory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var client = factory.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
            if (response.StatusCode != HttpStatusCode.NoContent)
                context.Fail("Access token is no longer current.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            context.Fail("Access token validation is unavailable.");
        }
    }
}
