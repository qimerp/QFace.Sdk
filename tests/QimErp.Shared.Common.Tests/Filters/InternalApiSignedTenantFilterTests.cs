using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using QimErp.Shared.Common.Filters;
using QimErp.Shared.Common.Options;
using Xunit;

namespace QimErp.Shared.Common.Tests.Filters;

public sealed class InternalApiSignedTenantFilterTests
{
    private const string Key = "test-shared-key";
    private const string Path = "/internal/employees/11111111-1111-1111-1111-111111111111/sync-projection";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    internal sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static DefaultHttpContext Signed()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET"; http.Request.Path = Path;
        http.Request.Headers[InternalServiceSignature.TenantHeader] = "tenant-a";
        http.Request.Headers[InternalServiceSignature.TimestampHeader] = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        http.Request.Headers[InternalServiceSignature.SignatureHeader] = InternalServiceSignature.Sign(Key, "tenant-a", "GET", Path, Now);
        return http;
    }

    private static async Task<(object? Result, bool Called)> Invoke(DefaultHttpContext http)
    {
        var filter = new InternalApiSignedTenantFilter(Microsoft.Extensions.Options.Options.Create(new InternalApiOptions { ExpectedApiKey = Key }), new Clock(Now));
        var called = false;
        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });
        return (result, called);
    }

    [Fact]
    public async Task Valid_signature_exposes_only_the_verified_tenant()
    {
        var http = Signed(); http.Request.QueryString = new QueryString("?tenantId=foreign");
        var result = await Invoke(http);
        result.Called.Should().BeTrue();
        http.GetVerifiedServiceTenant().Should().Be("tenant-a");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("key")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("minimum-long")]
    [InlineData("maximum-long")]
    public async Task Invalid_or_tampered_proof_never_invokes_owner_or_keeps_stale_identity(string mutation)
    {
        var http = Signed(); http.Items[InternalApiSignedTenantFilter.ItemKey] = "stale-tenant";
        switch (mutation)
        {
            case "tenant": http.Request.Headers[InternalServiceSignature.TenantHeader] = "foreign"; break;
            case "method": http.Request.Method = "POST"; break;
            case "path": http.Request.Path = Path + "/other"; break;
            case "key": http.Request.Headers[InternalServiceSignature.SignatureHeader] = InternalServiceSignature.Sign("wrong", "tenant-a", "GET", Path, Now); break;
            case "expired":
            case "future":
                var at = Now.AddMinutes(mutation == "expired" ? -3 : 3);
                http.Request.Headers[InternalServiceSignature.TimestampHeader] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                http.Request.Headers[InternalServiceSignature.SignatureHeader] = InternalServiceSignature.Sign(Key, "tenant-a", "GET", Path, at);
                break;
            case "malformed": http.Request.Headers[InternalServiceSignature.SignatureHeader] = "not-hex"; break;
            case "minimum-long": http.Request.Headers[InternalServiceSignature.TimestampHeader] = long.MinValue.ToString(); break;
            case "maximum-long": http.Request.Headers[InternalServiceSignature.TimestampHeader] = long.MaxValue.ToString(); break;
        }
        var result = await Invoke(http);
        result.Result.Should().BeOfType<UnauthorizedHttpResult>();
        result.Called.Should().BeFalse();
        http.GetVerifiedServiceTenant().Should().BeNull();
    }

    [Theory]
    [InlineData("X-Internal-Tenant")]
    [InlineData("X-Internal-Timestamp")]
    [InlineData("X-Internal-Signature")]
    public async Task Missing_or_multiple_proof_header_values_are_rejected(string header)
    {
        var http = Signed(); var original = http.Request.Headers[header].ToString();
        http.Request.Headers.Remove(header);
        (await Invoke(http)).Called.Should().BeFalse();
        http.Request.Headers[header] = new StringValues([original, original]);
        (await Invoke(http)).Result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task Unsigned_query_tenant_is_never_an_authentication_alternative()
    {
        var http = new DefaultHttpContext(); http.Request.QueryString = new QueryString("?tenantId=tenant-a");
        http.Request.Headers["X-Internal-Api-Key"] = Key;
        var result = await Invoke(http);
        result.Result.Should().BeOfType<UnauthorizedHttpResult>();
        result.Called.Should().BeFalse();
    }
}
