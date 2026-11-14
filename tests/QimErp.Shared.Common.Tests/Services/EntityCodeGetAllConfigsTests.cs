using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QimErp.Shared.Common.Database;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.ExceptionHandlers;
using QimErp.Shared.Common.Features.EntityCodes;
using QimErp.Shared.Common.Interceptors;
using QimErp.Shared.Common.Services;
using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Tests.TenantSetup;
using QimErp.Shared.Common.Workflow.Entities;
using Xunit;

namespace QimErp.Shared.Common.Tests.Services;

public sealed class EntityCodeGetAllConfigsTests : IDisposable
{
    private const string Tenant = "019e31ec-entcode-all-000000000001";
    private const string CompanyA = "company-a";
    private const string CompanyB = "company-b";
    private const string Module = "Leave";

    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    private static readonly Dictionary<string, (string, string, bool, int)> LeaveDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["LeaveRequest"] = ("LR", "-", true, 5),
            ["LeaveType"] = ("LT", "/", false, 3),
            ["LeavePolicy"] = ("LP", "-", false, 4),
        };

    private sealed class CountingDbContext(DbContextOptions<CountingDbContext> options, ITenantContext tenantContext)
        : ApplicationDbContext<CountingDbContext>(options, tenantContext)
    {
        public int QueryCount { get; set; }
        public int SaveCount { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<AppSetting>().Ignore(e => e.CustomFields);
            modelBuilder.Entity<EntityCodeConfig>().Ignore(e => e.CustomFields);
            modelBuilder.Entity<EntityCodeReservation>().Ignore(e => e.CustomFields);
            modelBuilder.Entity<TenantPluginFlag>().Ignore(e => e.CustomFields);
            modelBuilder.Entity<Import>().Ignore(e => e.CustomFields);
            modelBuilder.Entity<EntityWorkflowStep>().Ignore(e => e.CustomFields);
        }
    }

#pragma warning disable EF1001
    private sealed class CountingQueryCompiler(
        IQueryContextFactory queryContextFactory,
        ICompiledQueryCache compiledQueryCache,
        ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
        IDatabase database,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        ICurrentDbContext currentContext,
        IEvaluatableExpressionFilter evaluatableExpressionFilter,
        IModel model)
        : QueryCompiler(queryContextFactory, compiledQueryCache, compiledQueryCacheKeyGenerator,
            database, logger, currentContext, evaluatableExpressionFilter, model)
    {
        public override TResult Execute<TResult>(Expression query)
        {
            Count();
            return base.Execute<TResult>(query);
        }

        public override TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken = default)
        {
            Count();
            return base.ExecuteAsync<TResult>(query, cancellationToken);
        }

        private void Count()
        {
            if (currentContext.Context is CountingDbContext counting)
                counting.QueryCount++;
        }
    }
#pragma warning restore EF1001

    private sealed class SaveHook : SaveChangesInterceptor
    {
        public Func<DbContext, Task>? BeforeSave { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is CountingDbContext counting)
                counting.SaveCount++;
            if (BeforeSave is not null && eventData.Context is not null)
                await BeforeSave(eventData.Context);
            return result;
        }
    }

    private sealed class TestEntityCodeService(
        CountingDbContext context,
        string module,
        IReadOnlyDictionary<string, (string, string, bool, int)> defaults)
        : EntityCodeService<CountingDbContext>(
            context, new InMemoryDistributedCacheService(), NullLogger.Instance, module, defaults);

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;
        private readonly string _databaseName = $"entitycode-all-{Guid.NewGuid()}";
        private readonly ITenantContext _tenantContext;

        public CountingDbContext Db { get; }
        public SaveHook Hook { get; } = new();
        public TestEntityCodeService Service { get; }
        public UserContextService UserService { get; }

        public Harness(IReadOnlyDictionary<string, (string, string, bool, int)>? defaults = null)
        {
            var services = new ServiceCollection();
            services.AddHttpContextAccessor();
            services.AddScoped<UserContextService>();
            services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<UserContextService>());
            services.AddScoped<ITenantContext, TenantContext>();
            services.AddLogging();

            _root = services.BuildServiceProvider();
            _scope = _root.CreateScope();
            var sp = _scope.ServiceProvider;

            UserService = sp.GetRequiredService<UserContextService>();
            UserService.SetContext(Tenant, "tester@qimerp.com");
            _tenantContext = sp.GetRequiredService<ITenantContext>();
            _tenantContext.SetTenant(Tenant);

            var interceptor = new AuditEntitySaveChangesInterceptor(
                UserService, NullLogger<AuditEntitySaveChangesInterceptor>.Instance, sp);

            Db = new CountingDbContext(
                new DbContextOptionsBuilder<CountingDbContext>()
                    .UseInMemoryDatabase(_databaseName, DatabaseRoot)
                    .ReplaceService<IQueryCompiler, CountingQueryCompiler>()
                    .AddInterceptors(interceptor, Hook)
                    .Options,
                _tenantContext);

            Service = new TestEntityCodeService(Db, Module, defaults ?? LeaveDefaults);
        }

        public CountingDbContext NewContext() =>
            new(new DbContextOptionsBuilder<CountingDbContext>()
                    .UseInMemoryDatabase(_databaseName, DatabaseRoot)
                    .Options,
                _tenantContext);

        public async Task<List<EntityCodeConfig>> StoredConfigsAsync()
        {
            await using var db = NewContext();
            return await db.EntityCodeConfigs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        }

        public async Task InsertDirectAsync(EntityCodeConfig config)
        {
            await using var db = NewContext();
            db.EntityCodeConfigs.Add(config);
            await db.SaveChangesAsync();
        }

        public void ResetCounters()
        {
            Db.QueryCount = 0;
            Db.SaveCount = 0;
        }

        public void Dispose()
        {
            Db.Dispose();
            _scope.Dispose();
            _root.Dispose();
        }
    }

    private static void SetScope(CompanyScope scope) => new CompanyContext().SetScope(scope);

    public EntityCodeGetAllConfigsTests() => SetScope(CompanyScope.Inactive);

    public void Dispose() => new CompanyContext().Clear();

    [Fact(DisplayName = "With no configs stored, every known entity type is returned with its module defaults and persisted once")]
    public async Task NoConfigs_ReturnsDefaultsForEveryKnownType()
    {
        using var harness = new Harness();

        var configs = await harness.Service.GetAllConfigsAsync(Tenant);

        configs.Select(c => c.EntityType).Should().Equal("LeaveRequest", "LeaveType", "LeavePolicy");

        var request = configs.Single(c => c.EntityType == "LeaveRequest");
        request.Prefix.Should().Be("LR");
        request.Separator.Should().Be("-");
        request.IncludeYear.Should().BeTrue();
        request.PaddingWidth.Should().Be(5);

        var type = configs.Single(c => c.EntityType == "LeaveType");
        type.Prefix.Should().Be("LT");
        type.Separator.Should().Be("/");
        type.IncludeYear.Should().BeFalse();
        type.PaddingWidth.Should().Be(3);

        configs.Should().OnlyContain(c =>
            c.TenantId == Tenant
            && c.CompanyId == string.Empty
            && c.LastSequence == 0
            && c.ManualHighWaterMark == 0
            && c.Mode == CodeGenerationMode.Auto
            && c.ResetPeriod == CodeResetPeriod.Never);

        var stored = await harness.StoredConfigsAsync();
        stored.Should().HaveCount(3);
        stored.Should().OnlyContain(c => c.TenantId == Tenant && c.CompanyId == string.Empty);
    }

    [Fact(DisplayName = "Existing configs are returned as stored and never duplicated across repeated loads")]
    public async Task ExistingConfigs_AreNotDuplicated()
    {
        using var harness = new Harness();
        await harness.Service.UpsertConfigAsync(
            Tenant, "LeaveRequest", "LVR", "_", false, 6, CodeGenerationMode.Manual, CodeResetPeriod.Yearly);

        var first = await harness.Service.GetAllConfigsAsync(Tenant);
        var second = await harness.Service.GetAllConfigsAsync(Tenant);

        var customised = second.Single(c => c.EntityType == "LeaveRequest");
        customised.Prefix.Should().Be("LVR");
        customised.Separator.Should().Be("_");
        customised.PaddingWidth.Should().Be(6);
        customised.Mode.Should().Be(CodeGenerationMode.Manual);
        customised.ResetPeriod.Should().Be(CodeResetPeriod.Yearly);

        first.Select(c => c.Id).Should().BeEquivalentTo(second.Select(c => c.Id));

        var stored = await harness.StoredConfigsAsync();
        stored.Should().HaveCount(3);
        stored.GroupBy(c => (c.CompanyId, c.EntityType)).Should().OnlyContain(g => g.Count() == 1);
    }

    [Fact(DisplayName = "A company's first configs continue the tenant-wide sequence; types without a tenant-wide row start at zero")]
    public async Task CompanyConfigs_SeedFromTenantWideRow()
    {
        using var harness = new Harness();

        SetScope(CompanyScope.Inactive);
        await harness.Service.GenerateBatchAsync(Tenant, "LeaveRequest", 7);
        var tenantWideBefore = (await harness.StoredConfigsAsync()).Single();
        tenantWideBefore.LastSequence.Should().Be(7);

        SetScope(CompanyScope.ForCompanies([CompanyA, CompanyB], CompanyA));
        var configs = await harness.Service.GetAllConfigsAsync(Tenant);

        configs.Should().OnlyContain(c => c.CompanyId == CompanyA);
        configs.Single(c => c.EntityType == "LeaveRequest").LastSequence.Should().Be(7);
        configs.Single(c => c.EntityType == "LeaveType").LastSequence.Should().Be(0);
        configs.Single(c => c.EntityType == "LeavePolicy").LastSequence.Should().Be(0);

        var stored = await harness.StoredConfigsAsync();
        stored.Should().HaveCount(4);
        stored.Single(c => c.CompanyId == string.Empty).LastSequence.Should().Be(7);

        (await harness.Service.GenerateAsync(Tenant, "LeaveRequest"))
            .Should().EndWith("-00008");
    }

    [Fact(DisplayName = "When the company already has a config, it is returned instead of the tenant-wide row")]
    public async Task CompanyRow_WinsOverTenantWideRow()
    {
        using var harness = new Harness();

        var tenantWide = EntityCodeConfig.Create(Tenant, "LeaveRequest", "TW");
        tenantWide.IncrementSequenceBy(50);
        await harness.InsertDirectAsync(tenantWide);

        var companyRow = EntityCodeConfig.Create(Tenant, "LeaveRequest", "CO");
        companyRow.WithCompanyId(CompanyA);
        companyRow.IncrementSequenceBy(3);
        await harness.InsertDirectAsync(companyRow);

        SetScope(CompanyScope.ForCompanies([CompanyA, CompanyB], CompanyA));
        var configs = await harness.Service.GetAllConfigsAsync(Tenant);

        var request = configs.Single(c => c.EntityType == "LeaveRequest");
        request.Id.Should().Be(companyRow.Id);
        request.Prefix.Should().Be("CO");
        request.LastSequence.Should().Be(3);
    }

    [Fact(DisplayName = "Another company's rows are never returned or used as a seed")]
    public async Task OtherCompanyRows_AreIgnored()
    {
        using var harness = new Harness();

        var companyB = EntityCodeConfig.Create(Tenant, "LeaveRequest", "BBB");
        companyB.WithCompanyId(CompanyB);
        companyB.IncrementSequenceBy(40);
        await harness.InsertDirectAsync(companyB);

        SetScope(CompanyScope.ForCompanies([CompanyA, CompanyB], CompanyA));
        var configs = await harness.Service.GetAllConfigsAsync(Tenant);

        var request = configs.Single(c => c.EntityType == "LeaveRequest");
        request.CompanyId.Should().Be(CompanyA);
        request.Prefix.Should().Be("LR");
        request.LastSequence.Should().Be(0);
    }

    [Fact(DisplayName = "A two-company caller with no active company cannot auto-create tenant-shared configs from the list")]
    public async Task TwoCompanyCaller_NoActiveCompany_CannotCreateTenantSharedConfigs()
    {
        using var harness = new Harness();

        SetScope(CompanyScope.ForCompanies([CompanyA, CompanyB], active: null));

        var act = async () => await harness.Service.GetAllConfigsAsync(Tenant);
        await act.Should().ThrowAsync<AppSettingScopeViolationException>();

        (await harness.StoredConfigsAsync()).Should().BeEmpty();
        harness.Db.ChangeTracker.Entries<EntityCodeConfig>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A blank tenant id is refused before any query runs")]
    public async Task BlankTenant_IsRefused()
    {
        using var harness = new Harness();
        harness.ResetCounters();

        var act = async () => await harness.Service.GetAllConfigsAsync("  ");
        await act.Should().ThrowAsync<ArgumentException>();

        harness.Db.QueryCount.Should().Be(0);
    }

    [Fact(DisplayName = "Loading every config costs one query and one save no matter how many entity types the module has")]
    public async Task QueryCount_IsConstant_NotPerType()
    {
        var manyTypes = Enumerable.Range(1, 40)
            .ToDictionary(i => $"Entity{i:D2}", _ => ("ENT", "-", false, 4), StringComparer.OrdinalIgnoreCase);
        using var harness = new Harness(manyTypes);

        SetScope(CompanyScope.Inactive);
        await harness.Service.GenerateAsync(Tenant, "Entity01");

        SetScope(CompanyScope.ForCompanies([CompanyA, CompanyB], CompanyA));
        harness.ResetCounters();

        var created = await harness.Service.GetAllConfigsAsync(Tenant);

        created.Should().HaveCount(40);
        created.Single(c => c.EntityType == "Entity01").LastSequence.Should().Be(1);
        harness.Db.QueryCount.Should().Be(1);
        harness.Db.SaveCount.Should().Be(1);

        harness.ResetCounters();
        var reloaded = await harness.Service.GetAllConfigsAsync(Tenant);

        reloaded.Should().HaveCount(40);
        harness.Db.QueryCount.Should().Be(1);
        harness.Db.SaveCount.Should().Be(0);
    }

    [Fact(DisplayName = "When a concurrent request inserts the same configs first, the load re-reads and keeps the winner's rows")]
    public async Task ConcurrentInsert_ReReadsInsteadOfFailing()
    {
        using var harness = new Harness();
        var raceWinner = EntityCodeConfig.Create(Tenant, "LeaveType", "WIN");
        var raced = false;
        harness.Hook.BeforeSave = async _ =>
        {
            if (raced) return;
            raced = true;
            await harness.InsertDirectAsync(raceWinner);
            throw new DbUpdateException("duplicate key value violates unique constraint \"IX_EntityCodeConfigs_TenantId_CompanyId_EntityType\"");
        };

        var configs = await harness.Service.GetAllConfigsAsync(Tenant);

        configs.Select(c => c.EntityType).Should().Equal("LeaveRequest", "LeaveType", "LeavePolicy");
        var leaveType = configs.Single(c => c.EntityType == "LeaveType");
        leaveType.Id.Should().Be(raceWinner.Id);
        leaveType.Prefix.Should().Be("WIN");

        var stored = await harness.StoredConfigsAsync();
        stored.Should().HaveCount(3);
        stored.GroupBy(c => c.EntityType).Should().OnlyContain(g => g.Count() == 1);
    }

    [Fact(DisplayName = "A save that keeps failing is rethrown after the retry budget, not swallowed")]
    public async Task PersistentSaveFailure_IsRethrown()
    {
        using var harness = new Harness();
        harness.Hook.BeforeSave = _ => throw new DbUpdateException("persistent failure");

        var act = async () => await harness.Service.GetAllConfigsAsync(Tenant);

        await act.Should().ThrowAsync<DbUpdateException>().WithMessage("persistent failure");
        harness.Db.SaveCount.Should().Be(3);
        (await harness.StoredConfigsAsync()).Should().BeEmpty();
    }

    [Theory(DisplayName = "Module filter matching this service's module, case-insensitively, returns every config")]
    [InlineData("Leave")]
    [InlineData("leave")]
    [InlineData(" LEAVE ")]
    public async Task ModuleFilter_MatchingModule_ReturnsAll(string module)
    {
        using var harness = new Harness();

        var configs = await harness.Service.GetConfigsForModuleAsync(Tenant, module);

        configs.Select(c => c.EntityType).Should().Equal("LeaveRequest", "LeaveType", "LeavePolicy");
    }

    [Theory(DisplayName = "No module filter behaves exactly like GetAllConfigsAsync")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ModuleFilter_Blank_ReturnsAll(string? module)
    {
        using var harness = new Harness();

        var configs = await harness.Service.GetConfigsForModuleAsync(Tenant, module);

        configs.Select(c => c.EntityType).Should().Equal("LeaveRequest", "LeaveType", "LeavePolicy");
    }

    [Fact(DisplayName = "Module filter for another module returns nothing and neither queries nor creates configs")]
    public async Task ModuleFilter_OtherModule_LoadsNothing()
    {
        using var harness = new Harness();
        harness.ResetCounters();

        var configs = await harness.Service.GetConfigsForModuleAsync(Tenant, "Surveys");

        configs.Should().BeEmpty();
        harness.Db.QueryCount.Should().Be(0);
        harness.Db.SaveCount.Should().Be(0);
        (await harness.StoredConfigsAsync()).Should().BeEmpty();
    }

    [Fact(DisplayName = "The default interface filter keeps only configs whose GetModuleFor matches, case-insensitively")]
    public async Task DefaultInterfaceFilter_FiltersByGetModuleFor()
    {
        var leave = EntityCodeConfig.Create(Tenant, "LeaveRequest", "LR");
        var survey = EntityCodeConfig.Create(Tenant, "Survey", "SUR");
        var composite = new Mock<IEntityCodeService> { CallBase = true };
        composite.Setup(s => s.GetAllConfigsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntityCodeConfig> { leave, survey });
        composite.Setup(s => s.GetModuleFor("LeaveRequest")).Returns("Leave");
        composite.Setup(s => s.GetModuleFor("Survey")).Returns("Surveys");

        var surveysOnly = await composite.Object.GetConfigsForModuleAsync(Tenant, "surveys");
        var unfiltered = await composite.Object.GetConfigsForModuleAsync(Tenant, null);
        var unknown = await composite.Object.GetConfigsForModuleAsync(Tenant, "Payroll");

        surveysOnly.Should().ContainSingle().Which.Should().BeSameAs(survey);
        unfiltered.Should().Equal(leave, survey);
        unknown.Should().BeEmpty();
    }

    [Theory(DisplayName = "GET /api/entity-codes binds ?module= and returns only that module's entity types")]
    [InlineData("?module=leave", 3)]
    [InlineData("?module=Surveys", 0)]
    [InlineData("", 3)]
    public async Task Endpoint_ModuleQuery_FiltersResponse(string queryString, int expectedCount)
    {
        using var harness = new Harness();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IEntityCodeService>(harness.Service);
        builder.Services.AddSingleton<ICurrentUserService>(harness.UserService);
        await using var app = builder.Build();
        new EntityCodeSettingsEndpoint().AddRoutes(app);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/entity-codes"
                         && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"));

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        var modules = CollectStrings(json.RootElement, "module");
        modules.Should().HaveCount(expectedCount);
        modules.Where(m => m != Module).Should().BeEmpty();
    }

    private static List<string> CollectStrings(JsonElement element, string propertyName)
    {
        var found = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        found.Add(property.Value.GetString()!);
                    else
                        found.AddRange(CollectStrings(property.Value, propertyName));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    found.AddRange(CollectStrings(item, propertyName));
                break;
        }
        return found;
    }
}
