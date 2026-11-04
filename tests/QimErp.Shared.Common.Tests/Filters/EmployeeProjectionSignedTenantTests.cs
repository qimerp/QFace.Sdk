using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Filters;
using QimErp.Shared.Common.Options;
using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.Employees;
using Xunit;

namespace QimErp.Shared.Common.Tests.Filters;

public sealed class EmployeeProjectionSignedTenantTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_client_and_both_server_filters_agree_and_reject_tenant_tampering(bool tamper)
    {
        const string key = "test-shared-key";
        var now = DateTimeOffset.UtcNow;
        var clock = new InternalApiSignedTenantFilterTests.Clock(now);
        var options = Microsoft.Extensions.Options.Options.Create(new InternalApiOptions { ExpectedApiKey = key });
        var caller = new Mock<ICurrentUserService>(); caller.Setup(c => c.GetTenantId()).Returns("tenant-a");
        var seen = new List<string>();
        var handler = new Handler(async request =>
        {
            request.RequestUri!.Query.Should().BeEmpty();
            var http = new DefaultHttpContext(); http.Request.Method = request.Method.Method; http.Request.Path = request.RequestUri.AbsolutePath;
            foreach (var header in request.Headers) http.Request.Headers[header.Key] = header.Value.ToArray();
            if (tamper) http.Request.Headers[InternalServiceSignature.TenantHeader] = "foreign";
            var invocation = EndpointFilterInvocationContext.Create(http);
            var signed = new InternalApiSignedTenantFilter(options, clock);
            var apiKey = new InternalApiAuthFilter(options);
            var result = await apiKey.InvokeAsync(invocation, context => signed.InvokeAsync(context, _ =>
            {
                seen.Add(http.GetVerifiedServiceTenant()!);
                return ValueTask.FromResult<object?>(Results.Ok());
            }));
            return new HttpResponseMessage(result is UnauthorizedHttpResult ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EmployeeSyncProjection { EmployeeId = Guid.NewGuid(), TenantId = http.GetVerifiedServiceTenant() ?? "" })
            };
        });
        using var httpClient = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient(EmployeeSyncProjectionClient.HttpClientName)).Returns(httpClient);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["People:BaseUrl"] = "http://people.test" }).Build();
        var client = new EmployeeSyncProjectionClient(factory.Object, configuration, options, caller.Object,
            NullLogger<EmployeeSyncProjectionClient>.Instance, clock);
        if (tamper)
        {
            await FluentActions.Awaiting(() => client.GetAsync(Guid.NewGuid())).Should().ThrowAsync<HttpRequestException>();
            seen.Should().BeEmpty();
        }
        else
        {
            (await client.GetAsync(Guid.NewGuid()))!.TenantId.Should().Be("tenant-a");
            caller.Setup(c => c.GetTenantId()).Returns("tenant-b");
            (await client.GetAsync(Guid.NewGuid()))!.TenantId.Should().Be("tenant-b");
            seen.Should().Equal("tenant-a", "tenant-b");
            httpClient.DefaultRequestHeaders.Should().BeEmpty();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
