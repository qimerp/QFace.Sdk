using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using QimErp.Shared.Common.Constants;
using QimErp.Shared.Common.Filters;
using QimErp.Shared.Common.Integrations.Microsoft365;
using QimErp.Shared.Common.Services.Cache;
using Xunit;

namespace QimErp.Shared.Common.Tests.Integrations;

public sealed class Microsoft365ConnectionResolverTests
{
    private const string Tenant = "tenant-a";
    private const string Company = "441d988f-a99a-4ede-97fb-146d9f71a4b1";
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ProtectionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ProtectedCatalog_RoundTripsWithoutPlaintextCredentials()
    {
        var catalog = Catalog();
        var encrypted = Microsoft365CatalogProtection.Protect(catalog, ProtectionKey);

        encrypted.Should().NotContain("default-secret");
        Microsoft365CatalogProtection.Unprotect(encrypted, Tenant, ProtectionKey)
            .DefaultConnection!.Options!.ClientSecret.Should().Be("default-secret");
    }

    [Fact]
    public void ProtectedCatalog_UsesDifferentNoncesForEveryWrite()
    {
        Microsoft365CatalogProtection.Protect(Catalog(), ProtectionKey)
            .Should().NotBe(Microsoft365CatalogProtection.Protect(Catalog(), ProtectionKey));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("key")]
    [InlineData("ciphertext")]
    public void ProtectedCatalog_RejectsAuthenticationFailures(string change)
    {
        var payload = Microsoft365CatalogProtection.Protect(Catalog(), ProtectionKey);
        var key = change == "key" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : ProtectionKey;
        if (change == "ciphertext")
        {
            var bytes = Convert.FromBase64String(payload);
            bytes[^1] ^= 1;
            payload = Convert.ToBase64String(bytes);
        }
        var read = () => Microsoft365CatalogProtection.Unprotect(payload,
            change == "tenant" ? "tenant-b" : Tenant, key);

        read.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task CacheLoss_HydratesCompleteCatalogThroughSignedPlatformRequest()
    {
        var harness = new Harness();
        harness.OnRequest = request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be(Microsoft365CatalogOptions.HydrationPath);
            var timestamp = request.Headers.GetValues(InternalServiceSignature.TimestampHeader).Single();
            var signature = request.Headers.GetValues(InternalServiceSignature.SignatureHeader).Single();
            InternalServiceSignature.Verify("dedicated-hydration-key", Tenant, "POST",
                Microsoft365CatalogOptions.HydrationPath, timestamp, signature, TimeSpan.FromMinutes(1), Now)
                .Should().BeTrue();
            request.Content.Should().BeNull();
            harness.SetCatalog(Catalog());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var result = await harness.Resolver.ResolveAsync(Tenant, Company);

        result.Status.Should().Be(Microsoft365ConnectionStatus.Ready);
        result.SourceCompanyId.Should().BeEmpty();
        result.Options!.ClientId.Should().Be("default-client");
        harness.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task HydrationRedirect_IsUnavailableWithoutFollowingTheTarget()
    {
        var harness = new Harness();
        harness.OnRequest = _ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://untrusted.example/catalog") }
        };

        var result = await harness.Resolver.ResolveAsync(Tenant, Company);

        result.Status.Should().Be(Microsoft365ConnectionStatus.Unavailable);
        harness.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("incomplete")]
    [InlineData("tampered")]
    [InlineData("noncanonical-company")]
    public async Task UnverifiableCatalog_NeverFallsBackToDefault(string state)
    {
        var harness = new Harness();
        if (state != "missing")
            harness.SetCatalog(Catalog() with
            {
                IsComplete = state != "incomplete",
                ValidUntilUtc = state == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5)
            });
        if (state == "tampered")
            harness.Payload = "not-an-authenticated-catalog";
        if (state == "noncanonical-company")
            harness.SetCatalog(Catalog() with
            {
                CompanyConnections = ImmutableDictionary<string, Microsoft365ConnectionEntry>.Empty.Add(
                    Company.ToUpperInvariant(), new Microsoft365ConnectionEntry { Options = OptionsFor("company-client", "company-secret") })
            });
        harness.OnRequest = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var result = await harness.Resolver.ResolveAsync(Tenant, Company);

        result.Status.Should().Be(Microsoft365ConnectionStatus.Unavailable);
        result.Options.Should().BeNull();
        harness.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task StoredInvalidCompanyOverride_NeverUsesTenantDefault()
    {
        var harness = new Harness();
        harness.SetCatalog(Catalog() with
        {
            CompanyConnections = ImmutableDictionary<string, Microsoft365ConnectionEntry>.Empty
                .Add(Company, new Microsoft365ConnectionEntry { Error = "Cannot decrypt stored configuration" })
        });

        var result = await harness.Resolver.ResolveAsync(Tenant, Company);

        result.Status.Should().Be(Microsoft365ConnectionStatus.Invalid);
        result.SourceCompanyId.Should().Be(Company);
        result.Options.Should().BeNull();
        harness.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CompleteCatalogWithoutConnection_ReturnsNotConfigured()
    {
        var harness = new Harness();
        harness.SetCatalog(Catalog() with { DefaultConnection = null });

        var result = await harness.Resolver.ResolveAsync(Tenant, Company);

        result.Status.Should().Be(Microsoft365ConnectionStatus.NotConfigured);
        harness.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task InvalidCompanyId_IsRejectedWithoutDirectoryFallback()
    {
        var harness = new Harness();
        harness.SetCatalog(Catalog());

        var result = await harness.Resolver.ResolveAsync(Tenant, "not-a-company-id");

        result.Status.Should().Be(Microsoft365ConnectionStatus.Invalid);
        result.Options.Should().BeNull();
        harness.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingBusinessCompany_WithOverrides_IsRejected()
    {
        var harness = new Harness();
        harness.SetCatalog(Catalog() with
        {
            CompanyConnections = ImmutableDictionary<string, Microsoft365ConnectionEntry>.Empty.Add(Company,
                new Microsoft365ConnectionEntry { Options = OptionsFor("company-client", "company-secret") })
        });

        var result = await harness.Resolver.ResolveAsync(Tenant, string.Empty);

        result.Status.Should().Be(Microsoft365ConnectionStatus.Invalid);
        result.Options.Should().BeNull();
    }

    [Fact]
    public async Task GraphOperations_UseCompanyCredentialsAndDoNotFallbackAfterFailure()
    {
        var harness = new Harness();
        harness.SetCatalog(Catalog() with
        {
            CompanyConnections = ImmutableDictionary<string, Microsoft365ConnectionEntry>.Empty.Add(Company,
                new Microsoft365ConnectionEntry { Options = OptionsFor("company-client", "company-secret") })
        });
        harness.OnRequest = request =>
        {
            request.RequestUri!.Host.Should().Be("login.microsoftonline.com");
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            form.Should().Contain("client_id=company-client").And.Contain("client_secret=company-secret");
            form.Should().NotContain("default-client");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };
        var graph = new Microsoft365GraphClient(harness.Resolver, harness.Factory,
            NullLogger<Microsoft365GraphClient>.Instance);

        var result = await graph.TrySetAutomaticRepliesAsync(Tenant, Company, "employee@example.com",
            Now.UtcDateTime, Now.AddHours(1).UtcDateTime, "away", "away", false);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        harness.RequestCount.Should().Be(1);
    }

    private static Microsoft365ConnectionCatalog Catalog() => new()
    {
        TenantId = Tenant,
        Revision = "revision-1",
        IsComplete = true,
        ValidUntilUtc = Now.AddMinutes(5),
        DefaultConnection = new Microsoft365ConnectionEntry
        {
            Options = OptionsFor("default-client", "default-secret")
        }
    };

    private static TenantMicrosoft365ResolvedOptions OptionsFor(string clientId, string secret) => new()
    {
        EntraTenantId = "entra-directory",
        ClientId = clientId,
        ClientSecret = secret,
        EnableOutlookOofOnLeave = true
    };

    private sealed class Harness
    {
        public string? Payload { get; set; }
        public int RequestCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> OnRequest { get; set; } =
            _ => throw new InvalidOperationException("An unexpected HTTP request was made.");
        public IHttpClientFactory Factory { get; }
        public Microsoft365ConnectionResolver Resolver { get; }

        public Harness()
        {
            var cache = new Mock<ISharedDistributedCacheService>(MockBehavior.Strict);
            cache.Setup(c => c.GetAsync<string>(SharedCacheKeys.Microsoft365ConnectionCatalog(Tenant), null))
                .ReturnsAsync(() => Payload);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Handler(request =>
            {
                RequestCount++;
                return OnRequest(request);
            })));
            Factory = factory.Object;
            Resolver = new Microsoft365ConnectionResolver(cache.Object, Factory,
                Microsoft.Extensions.Options.Options.Create(new Microsoft365CatalogOptions
                {
                    PlatformOrigin = "https://platform.example.com",
                    HydrationKey = "dedicated-hydration-key",
                    ProtectionKey = ProtectionKey
                }), new FixedClock(), NullLogger<Microsoft365ConnectionResolver>.Instance);
        }

        public void SetCatalog(Microsoft365ConnectionCatalog catalog) =>
            Payload = Microsoft365CatalogProtection.Protect(catalog, ProtectionKey);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
