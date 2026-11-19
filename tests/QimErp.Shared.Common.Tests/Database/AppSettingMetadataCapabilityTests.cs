using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QimErp.Shared.Common.AppSettings.Authorization;
using QimErp.Shared.Common.Database;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Interceptors;
using QimErp.Shared.Common.Extensions;
using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.MultiTenancy;
using Xunit;

namespace QimErp.Shared.Common.Tests.Database;

public sealed class AppSettingMetadataCapabilityTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string CompanyA = "22222222-2222-2222-2222-222222222222";
    private const string CompanyB = "33333333-3333-3333-3333-333333333333";
    private const string Category = "Microsoft365Integration";
    private const string CompanyKey = "microsoft365.company_graph_config";
    private readonly TenantContext _tenant = new();
    private readonly ServiceProvider _services;
    private readonly MetadataContext _db;
    private readonly AppSettingMetadataScopeExecutor _executor;
    private readonly Clock _clock = new();

    public AppSettingMetadataCapabilityTests()
    {
        _tenant.SetTenant(Tenant);
        var user = new UserContextService(new Microsoft.AspNetCore.Http.HttpContextAccessor(), NullLogger<UserContextService>.Instance);
        user.SetContext(Tenant, "owner@example.com", userId: "owner");
        _executor = new(new Authorizer(), _tenant, user, _clock);
        _services = new ServiceCollection().AddSingleton<ICurrentUserService>(user)
            .AddSingleton<ITenantContext>(_tenant).AddLogging().BuildServiceProvider();
        _db = new MetadataContext(new DbContextOptionsBuilder<MetadataContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditEntitySaveChangesInterceptor(user,
                NullLogger<AuditEntitySaveChangesInterceptor>.Instance, _services)).Options, _tenant);
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
    }

    [Fact]
    public async Task ExactCompanyMetadataReadAndWrite_DoNotBroadenBusinessScope()
    {
        var original = CompanyContext.CurrentScope;
        await Run(AppSettingMetadataOperation.Write, CompanyB, async ct =>
        {
            _db.AppSettings.Add(Row(CompanyKey, CompanyB));
            await _db.SaveChangesAsync(ct);
            (await _db.AppSettings.CountAsync(ct)).Should().Be(1);
            CompanyContext.CurrentScope.Should().BeSameAs(original);
            return true;
        });
        (await _db.AppSettings.CountAsync()).Should().Be(0);
        (await Run(AppSettingMetadataOperation.Read, null, ct => _db.AppSettings.CountAsync(ct))).Should().Be(1);
        CompanyContext.CurrentScope.Should().BeSameAs(original);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-category")]
    [InlineData("wrong-company")]
    [InlineData("global")]
    [InlineData("wrong-tenant")]
    [InlineData("read-only")]
    public async Task Capability_RejectsNonMatchingWrites(string scenario)
    {
        var row = Row(scenario == "wrong-key" ? "other.setting" : CompanyKey,
            scenario == "wrong-company" ? CompanyA : CompanyB,
            scenario == "wrong-category" ? "Other" : Category);
        if (scenario == "global") row.EnableGlobal();
        if (scenario == "wrong-tenant") row.WithTenantId("44444444-4444-4444-4444-444444444444");
        var action = () => Run(scenario == "read-only" ? AppSettingMetadataOperation.Read : AppSettingMetadataOperation.Write,
            CompanyB, async ct => { _db.AppSettings.Add(row); await _db.SaveChangesAsync(ct); return true; });
        await action.Should().ThrowAsync<Exception>();
        AppSettingMetadataScopeExecutor.CurrentTenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadVisibility_IsExactKeyCategoryAndTenant_AndContextIsNotModelCached()
    {
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        _db.AppSettings.AddRange(Row(CompanyKey, CompanyB), Row("other.setting", CompanyB),
            Row(CompanyKey, CompanyB, "Other"));
        await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        (await Run(AppSettingMetadataOperation.Read, null, ct => _db.AppSettings.CountAsync(ct))).Should().Be(1);
        (await _db.AppSettings.CountAsync()).Should().Be(0);
        _tenant.SetTenant("44444444-4444-4444-4444-444444444444");
        var crossTenant = () => Run(AppSettingMetadataOperation.Read, null, ct => _db.AppSettings.CountAsync(ct));
        await crossTenant.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExceptionAndCancellation_RestoreCapability()
    {
        var action = () => Run<bool>(AppSettingMetadataOperation.Write, null, _ => throw new OperationCanceledException());
        await action.Should().ThrowAsync<OperationCanceledException>();
        AppSettingMetadataScopeExecutor.CurrentTenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpiredCapability_ThrowsInsteadOfReturningPartialCatalog()
    {
        await Run(AppSettingMetadataOperation.Read, null, async ct =>
        {
            _clock.Now += TimeSpan.FromSeconds(31);
            var query = () => _db.AppSettings.CountAsync(ct);
            await query.Should().ThrowAsync<Exception>();
            return true;
        });
        AppSettingMetadataScopeExecutor.CurrentTenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task CapabilityCopiedToAwaitedChild_IsRevokedAfterParentReturns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;
        await Run(AppSettingMetadataOperation.Read, null, _ =>
        {
            child = Task.Run(async () =>
            {
                await release.Task;
                var read = () => AppSettingMetadataScopeExecutor.CurrentTenantId;
                read.Should().Throw<UnauthorizedAccessException>();
            });
            return Task.FromResult(true);
        });
        release.SetResult();
        await child!;
    }

    [Fact]
    public async Task ChangedTenantInsideAction_RevokesMetadataAuthority()
    {
        await Run(AppSettingMetadataOperation.Read, null, _ =>
        {
            _tenant.SetTenant("44444444-4444-4444-4444-444444444444");
            var read = () => AppSettingMetadataScopeExecutor.CurrentTenantId;
            read.Should().Throw<UnauthorizedAccessException>();
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task ExactDefaultTenantOnlyWrite_IsSharedEvenWithSelectedCompany()
    {
        await Run(AppSettingMetadataOperation.Write, null, async ct =>
        {
            var row = Row("microsoft365.graph_config", string.Empty).WithScope(AppSettingScope.TenantOnly);
            _db.AppSettings.Add(row);
            await _db.SaveChangesAsync(ct);
            row.CompanyId.Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task RealBusinessEntityOutsideCompanyScope_RemainsDenied()
    {
        await Run(AppSettingMetadataOperation.Write, CompanyB, async ct =>
        {
            var row = EntityCodeConfig.Create(Tenant, "Invoice");
            row.WithCompanyId(CompanyB);
            _db.EntityCodeConfigs.Add(row);
            var save = () => _db.SaveChangesAsync(ct);
            await save.Should().ThrowAsync<Exception>();
            return true;
        });
    }

    [Fact]
    public async Task RealBusinessReadOutsideCompanyScope_RemainsHidden()
    {
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        var row = EntityCodeConfig.Create(Tenant, "Invoice"); row.WithCompanyId(CompanyB);
        _db.EntityCodeConfigs.Add(row); await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        (await Run(AppSettingMetadataOperation.Read, null, ct => _db.EntityCodeConfigs.CountAsync(ct)))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("category")]
    [InlineData("company")]
    [InlineData("scope")]
    public async Task OriginalOwnership_CannotBeRelabelledIntoCapability(string change)
    {
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        var row = Row(change == "key" ? "other.setting" : CompanyKey,
            change == "company" ? CompanyA : CompanyB, change == "category" ? "Other" : Category);
        if (change == "scope") row.WithScope(AppSettingScope.TenantOnly).AsTenantShared();
        _db.AppSettings.Add(row);
        await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        await Run(AppSettingMetadataOperation.Write, CompanyB, async ct =>
        {
            _db.Entry(row).Property(x => x.Key).CurrentValue = CompanyKey;
            _db.Entry(row).Property(x => x.Category).CurrentValue = Category;
            row.WithCompanyId(CompanyB); row.WithScope(AppSettingScope.CompanyOverridable);
            var save = () => _db.SaveChangesAsync(ct);
            await save.Should().ThrowAsync<Exception>();
            return true;
        });
    }

    [Fact]
    public async Task RelationalMetadataFilter_IsParameterizedPerContextAndRestoresAfterScope()
    {
        using var relational = new MetadataContext(new DbContextOptionsBuilder<MetadataContext>()
            .UseQimErpNpgsql("Host=localhost;Database=metadata_tests;Username=none;Password=none").Options, _tenant);
        var before = relational.AppSettings.ToQueryString();
        before.Should().Contain("__ef_filter__");
        await Run(AppSettingMetadataOperation.Read, null, _ =>
        {
            var during = relational.AppSettings.ToQueryString();
            during.Should().Contain("microsoft365.company_graph_config").And.Contain(CompanyB);
            return Task.FromResult(true);
        });
        relational.AppSettings.ToQueryString().Should().Be(before);
        var otherTenant = new TenantContext(); otherTenant.SetTenant("44444444-4444-4444-4444-444444444444");
        using var other = new MetadataContext(new DbContextOptionsBuilder<MetadataContext>()
            .UseQimErpNpgsql("Host=localhost;Database=metadata_tests;Username=none;Password=none").Options, otherTenant);
        other.AppSettings.ToQueryString().Should().Contain("44444444-4444-4444-4444-444444444444");
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("not-authorized-id", false)]
    [InlineData("wrong-actor", false)]
    [InlineData("wrong-tenant", false)]
    [InlineData("wrong-target", false)]
    [InlineData("extra-property", false)]
    [InlineData("move-company", false)]
    [InlineData("move-user", false)]
    [InlineData("global", false)]
    [InlineData("delete", false)]
    [InlineData("read-only", false)]
    public async Task IdentitySessionRevocation_IsExactAndDoesNotGrantOtherEdits(string scenario, bool allowed)
    {
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        var row = SessionRow.Create(); row.WithTenantId(Tenant).WithCompanyId(CompanyB);
        _db.SessionRows.Add(row); await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        await Run(scenario == "read-only" ? AppSettingMetadataOperation.Read : AppSettingMetadataOperation.Write,
            null, async ct =>
        {
            _db.RevocationTenantId = scenario == "wrong-tenant" ? "other" : Tenant;
            _db.RevocationActorId = scenario == "wrong-actor" ? "other" : "owner";
            _db.RevocationSessionIds = scenario == "not-authorized-id" ? [] : [row.Id.ToString()];
            _db.RevocationTargetCompanyId = scenario == "wrong-target" ? CompanyB : null;
            row.IsActive = false;
            if (scenario == "extra-property") row.Label = "changed";
            if (scenario == "move-company") row.WithCompanyId(CompanyA);
            if (scenario == "move-user") row.UserId = "other";
            if (scenario == "global") row.EnableGlobal();
            if (scenario == "delete") _db.SessionRows.Remove(row);
            var save = () => _db.SaveChangesAsync(ct);
            if (allowed) await save.Should().NotThrowAsync();
            else await save.Should().ThrowAsync<Exception>();
            return true;
        });
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-actor")]
    [InlineData("invalid-company")]
    [InlineData("unknown-policy")]
    [InlineData("unknown-operation")]
    public async Task MalformedAuthorityOrPolicy_IsRejectedBeforeAction(string scenario)
    {
        var user = _services.GetRequiredService<ICurrentUserService>();
        var executor = new AppSettingMetadataScopeExecutor(new GrantAuthorizer(new(
            scenario == "wrong-actor" ? "other" : "owner",
            scenario == "wrong-tenant" ? "44444444-4444-4444-4444-444444444444" : Tenant,
            scenario == "invalid-company" ? ["not-a-guid"] : [CompanyA, CompanyB])), _tenant, user);
        var ran = false;
        var action = () => executor.RunAsync(scenario == "unknown-policy" ? "Other" : AppSettingMetadataPolicy.Microsoft365OwnerManagement,
            scenario == "unknown-operation" ? (AppSettingMetadataOperation)99 : AppSettingMetadataOperation.Write,
            null, _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);
        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task HistoricSharedDefaultScope_CanUpgradeOnlyToTenantOnly()
    {
        new CompanyContext().SetScope(CompanyScope.Inactive);
        var row = Row("microsoft365.graph_config", string.Empty);
        _db.AppSettings.Add(row); await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        await Run(AppSettingMetadataOperation.Write, null, async ct =>
        {
            row.WithScope(AppSettingScope.TenantOnly); row.UpdateValue("new-protected-value");
            await _db.SaveChangesAsync(ct); return true;
        });
        await Run(AppSettingMetadataOperation.Write, null, async ct =>
        {
            row.WithScope(AppSettingScope.CompanyOverridable);
            var save = () => _db.SaveChangesAsync(ct);
            await save.Should().ThrowAsync<Exception>(); return true;
        });
    }

    [Fact]
    public async Task ExactCompanyDelete_IsAllowedWithoutChangingScope()
    {
        var scope = CompanyContext.CurrentScope;
        await Run(AppSettingMetadataOperation.Write, CompanyB, async ct =>
        {
            var row = Row(CompanyKey, CompanyB); _db.AppSettings.Add(row); await _db.SaveChangesAsync(ct);
            _db.AppSettings.Remove(row); await _db.SaveChangesAsync(ct);
            return true;
        });
        CompanyContext.CurrentScope.Should().BeSameAs(scope);
    }

    private sealed class GrantAuthorizer(AppSettingMetadataAuthorization grant) : IAppSettingMetadataAuthorizer
    {
        public Task<AppSettingMetadataAuthorization> AuthorizeAsync(string policyName,
            AppSettingMetadataOperation operation, string? targetCompanyId, CancellationToken cancellationToken) => Task.FromResult(grant);
    }

    [Fact]
    public async Task ActorChangedInsideCapability_CannotQueryOrWrite()
    {
        var user = _services.GetRequiredService<ICurrentUserService>();
        await Run(AppSettingMetadataOperation.Write, CompanyB, async ct =>
        {
            user.SetContext(Tenant, "other@example.com", userId: "other");
            try
            {
                var metadata = () => AppSettingMetadataScopeExecutor.CurrentTenantId;
                metadata.Should().Throw<UnauthorizedAccessException>();
                var query = () => _db.AppSettings.CountAsync(ct);
                await query.Should().ThrowAsync<Exception>();
                _db.AppSettings.Add(Row(CompanyKey, CompanyB));
                var save = () => _db.SaveChangesAsync(ct);
                await save.Should().ThrowAsync<Exception>();
            }
            finally { user.SetContext(Tenant, "owner@example.com", userId: "owner"); }
            return true;
        });
    }

    [Fact]
    public async Task MetadataFilter_PreservesAdditionalApplicationPredicate()
    {
        var user = _services.GetRequiredService<ICurrentUserService>();
        using var db = new PredicateContext(new DbContextOptionsBuilder<PredicateContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditEntitySaveChangesInterceptor(user,
                NullLogger<AuditEntitySaveChangesInterceptor>.Instance, _services)).Options, _tenant);
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        var excluded = Row(CompanyKey, CompanyB); excluded.UpdateValue("excluded");
        db.AppSettings.AddRange(excluded, Row("microsoft365.company_sign_in_binding", CompanyB));
        await db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        var rows = await Run(AppSettingMetadataOperation.Read, null, ct => db.AppSettings.ToListAsync(ct));
        rows.Should().ContainSingle().Which.Key.Should().Be("microsoft365.company_sign_in_binding");
        (await db.AppSettings.CountAsync()).Should().Be(0);
    }

    private sealed class PredicateContext(DbContextOptions<PredicateContext> options, ITenantContext tenant)
        : ApplicationDbContext<PredicateContext>(options, tenant), IAppSettingMetadataQueryContext
    {
        public string MetadataTenantId => AppSettingMetadataScopeExecutor.CurrentTenantId;
        public string MetadataCategory => AppSettingMetadataScopeExecutor.CurrentCategory;
        public string[] MetadataKeys => AppSettingMetadataScopeExecutor.CurrentKeys;
        public string[] MetadataReadCompanyIds => AppSettingMetadataScopeExecutor.CurrentReadCompanyIds;
        public bool MetadataReadAllCompanies => AppSettingMetadataScopeExecutor.CurrentReadAllCompanies;
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AppSetting>().HasQueryFilter(row => row.Value != "excluded");
        }
    }

    [Fact]
    public async Task CompleteMetadataRead_IncludesOrphanAndMalformedOwnership_WithoutGrantingWrites()
    {
        const string orphan = "55555555-5555-5555-5555-555555555555";
        new CompanyContext().SetScope(CompanyScope.AllCompanies(null));
        var sameTenantGlobal = Row("microsoft365.company_sign_in_binding", orphan); sameTenantGlobal.EnableGlobal();
        var foreign = Row("microsoft365.company_sign_in_binding", orphan);
        foreign.WithTenantId("44444444-4444-4444-4444-444444444444"); foreign.EnableGlobal();
        _db.AppSettings.AddRange(Row(CompanyKey, orphan), Row(CompanyKey, "malformed-company"),
            sameTenantGlobal, foreign, Row("other.setting", orphan), Row(CompanyKey, orphan, "Other"));
        await _db.SaveChangesAsync();
        new CompanyContext().SetScope(CompanyScope.ForCompanies([CompanyA], CompanyA));
        var rows = await Run(AppSettingMetadataOperation.Read, null, ct => _db.AppSettings.ToListAsync(ct));
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(row => row.TenantId == Tenant && row.Category == Category);
        rows.Should().Contain(row => row.CompanyId == "malformed-company");
        rows.Should().Contain(row => row.IsGlobal);
        (await _db.AppSettings.CountAsync()).Should().Be(0);
        foreach (var target in new[] { orphan, "malformed-company" })
        {
            var write = () => Run(AppSettingMetadataOperation.Write, target, _ => Task.FromResult(true));
            await write.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    public sealed class SessionRow : GuidAuditableEntity, IIdentitySessionRevocationTarget
    {
        public string UserId { get; set; } = "target";
        public bool IsActive { get; set; } = true;
        public string Label { get; set; } = "unchanged";
        public static SessionRow Create() => new() { Id = Guid.NewGuid() };
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Task<T> Run<T>(AppSettingMetadataOperation op, string? company,
        Func<CancellationToken, Task<T>> action) => _executor.RunAsync(
        AppSettingMetadataPolicy.Microsoft365OwnerManagement, op, company, action, CancellationToken.None);
    private static AppSetting Row(string key, string company, string category = Category)
    {
        var row = AppSetting.Create(key, "protected", category);
        row.WithTenantId(Tenant).WithCompanyId(company);
        return row;
    }
    public void Dispose() { _db.Dispose(); _services.Dispose(); new CompanyContext().Clear(); _tenant.SetTenant(string.Empty); }
    private sealed class Authorizer : IAppSettingMetadataAuthorizer
    {
        public Task<AppSettingMetadataAuthorization> AuthorizeAsync(string policyName,
            AppSettingMetadataOperation operation, string? targetCompanyId, CancellationToken cancellationToken) =>
            Task.FromResult(new AppSettingMetadataAuthorization("owner", Tenant, [CompanyA, CompanyB]));
    }
    private sealed class MetadataContext(DbContextOptions<MetadataContext> options, ITenantContext tenant)
        : ApplicationDbContext<MetadataContext>(options, tenant), IAppSettingMetadataQueryContext, IIdentitySessionRevocationWriteContext
    {
        public DbSet<SessionRow> SessionRows => Set<SessionRow>();
        public string RevocationTenantId { get; set; } = string.Empty;
        public string RevocationActorId { get; set; } = string.Empty;
        public string? RevocationTargetCompanyId { get; set; }
        public string[] RevocationSessionIds { get; set; } = [];
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<SessionRow>().Ignore(x => x.CustomFields);
        }
        public string MetadataTenantId => AppSettingMetadataScopeExecutor.CurrentTenantId;
        public string MetadataCategory => AppSettingMetadataScopeExecutor.CurrentCategory;
        public string[] MetadataKeys => AppSettingMetadataScopeExecutor.CurrentKeys;
        public string[] MetadataReadCompanyIds => AppSettingMetadataScopeExecutor.CurrentReadCompanyIds;
        public bool MetadataReadAllCompanies => AppSettingMetadataScopeExecutor.CurrentReadAllCompanies;
    }
}
