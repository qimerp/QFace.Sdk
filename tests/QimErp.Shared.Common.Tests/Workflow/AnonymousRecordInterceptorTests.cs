using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QimErp.Shared.Common.Database.Configurations;
using QimErp.Shared.Common.Entities;
using QimErp.Shared.Common.Events;
using QimErp.Shared.Common.Interceptors;
using QimErp.Shared.Common.Options;
using QimErp.Shared.Common.Outbox;
using QimErp.Shared.Common.Outbox.Transports;
using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.MultiTenancy;
using QimErp.Shared.Common.Services.TenantSetup;
using QimErp.Shared.Common.Services.Workflow;
using QimErp.Shared.Common.Tests.Logging;
using QimErp.Shared.Common.Workflow;
using QimErp.Shared.Common.Workflow.Entities;
using QimErp.Shared.Common.Workflow.Enums;
using Xunit;

namespace QimErp.Shared.Common.Tests.Workflow;

public sealed class AnonymousRecordInterceptorTests
{
    private const string TenantId = "anonymous-record-tenant";
    private const string ModuleName = "TestModule";
    private const string ActorId = "test-user-id";
    private const string ActorEmail = "tester@qimerp.com";

    [Fact]
    public async Task An_Anonymous_Record_Writes_No_Outbox_Row_Starts_No_Workflow_And_Keeps_No_Actor()
    {
        await using var harness = new AnonymousRecordHarness();
        harness.RequireCreateWorkflow();
        var entity = AnonymousRecordEntity.Create(anonymous: true);

        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        (await harness.Context.OutboxMessages.ToListAsync()).Should().BeEmpty();
        entity.WorkflowStatus.Should().Be(WorkflowStatus.NotStarted);
        entity.WorkflowCode.Should().BeNull();
        entity.CurrentWorkflowHistoryId.Should().BeNull();
        AssertNoActor(entity);
    }

    [Fact]
    public async Task The_Same_Record_When_Identified_Still_Records_The_Actor_And_The_Workflow()
    {
        await using var harness = new AnonymousRecordHarness();
        harness.RequireCreateWorkflow();
        var entity = AnonymousRecordEntity.Create(anonymous: false);

        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        var rows = await harness.Context.OutboxMessages.ToListAsync();
        rows.Should().Contain(row => row.EventType == nameof(WorkflowStatusChangedEvent) && row.Payload.Contains(ActorId));
        rows.Should().Contain(row => row.EventType == WorkflowApprovalOutboxTemporalHandler.EventTypeName);
        entity.WorkflowStatus.Should().Be(WorkflowStatus.InProgress);
        entity.CreatedByUserId.Should().Be(ActorId);
    }

    [Fact]
    public async Task A_Modified_Anonymous_Record_Writes_No_Status_Row_And_Keeps_No_Actor()
    {
        await using var harness = new AnonymousRecordHarness();
        var entity = AnonymousRecordEntity.Create(anonymous: true);
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.Note = "changed";
        await harness.Context.SaveChangesAsync();

        (await harness.Context.OutboxMessages.ToListAsync()).Should().BeEmpty();
        AssertNoActor(entity);
    }

    [Fact]
    public async Task A_Record_Made_Anonymous_On_Update_Loses_Its_Actor_And_Exact_Times()
    {
        await using var harness = new AnonymousRecordHarness();
        var entity = AnonymousRecordEntity.Create(anonymous: false);
        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();
        var rowsBefore = await harness.Context.OutboxMessages.CountAsync();

        entity.Anonymous = true;
        await harness.Context.SaveChangesAsync();

        (await harness.Context.OutboxMessages.CountAsync()).Should().Be(rowsBefore);
        AssertNoActor(entity);
    }

    [Fact]
    public async Task An_Anonymous_Record_That_Raises_A_Domain_Event_Is_Refused()
    {
        await using var harness = new AnonymousRecordHarness();
        var entity = AnonymousRecordEntity.Create(anonymous: true);
        entity.AddDomainEvent(new RaisedEvent());
        harness.Context.Add(entity);

        var save = () => harness.Context.SaveChangesAsync();

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*anonymous*");
        harness.Context.ChangeTracker.Clear();
        (await harness.Context.OutboxMessages.ToListAsync()).Should().BeEmpty();
        (await harness.Context.Entities.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Interceptor_Logs_Do_Not_Carry_An_Anonymous_Record_Id()
    {
        var sink = new LogSink();
        await using var harness = new AnonymousRecordHarness(sink.Logger<AuditEntitySaveChangesInterceptor>());
        harness.RequireCreateWorkflow();
        var entity = AnonymousRecordEntity.Create(anonymous: true);
        entity.TenantId = string.Empty;

        harness.Context.Add(entity);
        await harness.Context.SaveChangesAsync();

        entity.TenantId.Should().Be(TenantId);
        sink.Entries.Should().NotBeEmpty();
        sink.Entries.Should().NotContain(log => log.Text.Contains(entity.Id.ToString()));
    }

    private static void AssertNoActor(AnonymousRecordEntity entity)
    {
        entity.CreatedByUserId.Should().Be(IAnonymousRecord.Actor);
        entity.LastModifiedByUserId.Should().Be(IAnonymousRecord.Actor);
        entity.EntityStatusLastModifiedByUserId.Should().Be(IAnonymousRecord.Actor);
        entity.CreatedByEmail.Should().BeEmpty();
        entity.LastModifiedByEmail.Should().BeEmpty();
        entity.EntityStatusLastModifiedByEmail.Should().BeEmpty();
        entity.CreatedByEmail.Should().NotBe(ActorEmail);
        entity.Created.Should().Be(entity.Created.Date);
        entity.Created.Kind.Should().Be(DateTimeKind.Utc);
        entity.LastModified.Should().Be(entity.LastModified?.Date);
        if (entity.EntityStatusCreated is { } statusCreated)
            statusCreated.Should().Be(statusCreated.Date);
        if (entity.EntityStatusLastModified is { } statusModified)
            statusModified.Should().Be(statusModified.Date);
    }

    private sealed record RaisedEvent : IDomainEvent
    {
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
        public string TenantId { get; init; } = AnonymousRecordInterceptorTests.TenantId;
        public string? TriggeredBy { get; } = "tester";
    }

    internal sealed class AnonymousRecordEntity : WorkflowEnabledEntity, IAnonymousRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Note { get; set; } = string.Empty;
        public bool Anonymous { get; set; }
        public bool IsAnonymousRecord => Anonymous;

        public static AnonymousRecordEntity Create(bool anonymous)
        {
            var entity = new AnonymousRecordEntity { Anonymous = anonymous, Note = "initial" };
            entity.TenantId = AnonymousRecordInterceptorTests.TenantId;
            entity.AsActive();
            return entity;
        }
    }

    internal sealed class AnonymousRecordDbContext(DbContextOptions<AnonymousRecordDbContext> options) : DbContext(options)
    {
        public DbSet<AnonymousRecordEntity> Entities => Set<AnonymousRecordEntity>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AnonymousRecordEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TenantId).IsRequired();
                entity.Ignore(e => e.CustomFields);
                entity.Ignore(e => e.DomainEvents);
            });
            modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        }
    }

    private sealed class AnonymousRecordHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly FakeWorkflowConfigCacheService _configCache = new();
        private readonly FakeWorkflowDefinitionProvider _definitions = new(TenantId);

        private sealed class BoundOutbox : IOutbox
        {
            private EfOutbox? _inner;

            public void Bind(DbContext db, ITransportRouting routing, TimeProvider clock) =>
                _inner = new EfOutbox(db, routing, clock);

            public void Add(IDomainEvent domainEvent, Guid entityId, long entityVersion = 0) =>
                Require().Add(domainEvent, entityId, entityVersion);

            public void Add(string eventType, string tenantId, Guid entityId, long entityVersion, object payload) =>
                Require().Add(eventType, tenantId, entityId, entityVersion, payload);

            private EfOutbox Require() =>
                _inner ?? throw new InvalidOperationException("Outbox not bound.");
        }

        public AnonymousRecordHarness(ILogger<AuditEntitySaveChangesInterceptor>? logger = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workflow:Module"] = ModuleName })
                .Build();

            var bound = new BoundOutbox();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ICurrentUserService>(new FakeCurrentUserService(TenantId));
            services.AddSingleton<ITenantContext>(new TenantContext());
            services.AddSingleton<IWorkflowConfigCacheService>(_configCache);
            services.AddSingleton<IWorkflowDefinitionProvider>(_definitions);
            services.AddSingleton<IWorkflowTriggerBridge>(new RecordingWorkflowTriggerBridge());
            services.AddSingleton<ITenantModuleAccessService>(new FakeTenantModuleAccessService());
            services.AddSingleton<IWorkflowService, WorkflowService>();
            services.AddSingleton<IOutbox>(bound);
            services.Configure<OutboxOptions>(_ => { });
            services.Configure<SystemOptions>(options =>
            {
                options.DefaultUserId = "test-user";
                options.DefaultSystemEmail = ActorEmail;
                options.DefaultUserName = "Tester";
            });
            _serviceProvider = services.BuildServiceProvider();

            var interceptor = new AuditEntitySaveChangesInterceptor(
                _serviceProvider.GetRequiredService<ICurrentUserService>(),
                logger ?? _serviceProvider.GetRequiredService<ILogger<AuditEntitySaveChangesInterceptor>>(),
                _serviceProvider,
                configuration);

            var options = new DbContextOptionsBuilder<AnonymousRecordDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;
            Context = new AnonymousRecordDbContext(options);
            bound.Bind(Context, new TransportRouting(_serviceProvider.GetRequiredService<IOptions<OutboxOptions>>()), TimeProvider.System);
        }

        public AnonymousRecordDbContext Context { get; }

        public void RequireCreateWorkflow()
        {
            _configCache.SetConfig(
                ModuleName,
                new EntityWorkflowConfig { EnableWorkflowForCreate = true, CreateWorkflowCode = "CREATE-WF" },
                nameof(AnonymousRecordEntity));
            _definitions.SetDefinition("CREATE-WF", new PublishedWorkflowDefinition
            {
                EntityType = nameof(AnonymousRecordEntity),
                IsActive = true,
                Definition = new WorkflowDefinition
                {
                    Steps = [new WorkflowStep { StepCode = "CREATE-STEP", Name = "CREATE-STEP", Order = 1 }]
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            Context.Dispose();
            _serviceProvider.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
