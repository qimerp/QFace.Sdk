using Microsoft.Extensions.Options;
using QimErp.Shared.Common.Options;

namespace QimErp.Shared.Common.Filters;

public sealed class InternalApiSignedTenantFilter(IOptions<InternalApiOptions> options, TimeProvider timeProvider) : IEndpointFilter
{
    public const string ItemKey = "QimErp.VerifiedServiceTenant";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        http.Items.Remove(ItemKey);
        var headers = http.Request.Headers;
        var tenants = headers[InternalServiceSignature.TenantHeader];
        var timestamps = headers[InternalServiceSignature.TimestampHeader];
        var signatures = headers[InternalServiceSignature.SignatureHeader];
        if (tenants.Count != 1 || timestamps.Count != 1 || signatures.Count != 1
            || string.IsNullOrWhiteSpace(tenants[0]) || string.IsNullOrEmpty(timestamps[0]) || string.IsNullOrEmpty(signatures[0]))
            return Results.Unauthorized();
        var tenant = tenants[0]!.Trim();
        if (!InternalServiceSignature.Verify(options.Value.ExpectedApiKey, tenant, http.Request.Method,
                http.Request.Path.Value ?? string.Empty, timestamps[0]!, signatures[0]!,
                options.Value.SignedTenantValidity, timeProvider.GetUtcNow()))
            return Results.Unauthorized();
        http.Items[ItemKey] = tenant;
        return await next(context);
    }
}

public static class VerifiedServiceTenantExtensions
{
    public static string? GetVerifiedServiceTenant(this HttpContext httpContext) =>
        httpContext.Items.TryGetValue(InternalApiSignedTenantFilter.ItemKey, out var value) ? value as string : null;
}
