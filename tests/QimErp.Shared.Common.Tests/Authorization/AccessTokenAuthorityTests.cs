using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QimErp.Shared.Common.Authorization;
using QimErp.Shared.Common.Filters;
using Xunit;

namespace QimErp.Shared.Common.Tests.Authorization;

public sealed class AccessTokenAuthorityTests
{
    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.Redirect, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task SendsOnlySignedIdentityAndRejectsAnyNonSuccessAuthorityResponse(HttpStatusCode status, bool valid)
    {
        var tenant = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var generation = new string('A', 64);
        const string key = "test-authority-key";
        using var handler = new CaptureHandler(async request =>
        {
            Assert.Equal($"https://platform.test/internal/auth/access-generation/{subject:D}", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            var header = InternalServiceSignature.TenantHeader;
            Assert.Equal(tenant.ToString("D"), request.Headers.GetValues(header).Single());
            Assert.True(InternalServiceSignature.Verify(key, tenant.ToString("D"), "POST", request.RequestUri.AbsolutePath,
                request.Headers.GetValues(InternalServiceSignature.TimestampHeader).Single(),
                request.Headers.GetValues(InternalServiceSignature.SignatureHeader).Single(),
                TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow));
            Assert.Equal($$"""{"generation":"{{generation}}"}""", await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(status);
        });
        using var services = new ServiceCollection().AddSingleton<IHttpClientFactory>(new Factory(handler)).BuildServiceProvider();
        var context = CreateContext(services, tenant, subject, generation);

        await new AccessTokenAuthority(new Uri("https://platform.test"), key).ValidateAsync(context);

        Assert.Equal(valid, context.Result is null);
    }

    [Fact]
    public async Task RejectsLegacyTokenWithoutCallingAuthority()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = CreateContext(services, Guid.NewGuid(), Guid.NewGuid(), null);
        await new AccessTokenAuthority(new Uri("https://platform.test"), "key").ValidateAsync(context);
        Assert.NotNull(context.Result?.Failure);
    }

    private static TokenValidatedContext CreateContext(IServiceProvider services, Guid tenant, Guid subject, string? generation)
    {
        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = services },
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), new JwtBearerOptions());
        var claims = new List<Claim> { new("tenantId", tenant.ToString()), new(ClaimTypes.NameIdentifier, subject.ToString()) };
        if (generation is not null)
            claims.Add(new Claim("access_generation", generation));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return context;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
