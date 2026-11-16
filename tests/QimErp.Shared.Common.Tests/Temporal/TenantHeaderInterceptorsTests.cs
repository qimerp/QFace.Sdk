using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QFace.Sdk.Temporal.Extensions;
using QFace.Sdk.Temporal.Interceptors;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace QimErp.Shared.Common.Tests.Temporal;

public sealed class TenantHeaderInterceptorsTests
{
    private const string TaskQueue = "tenant-header-interceptors-test";

    [Fact]
    public async Task The_tenant_given_to_the_client_reaches_the_activity_as_the_ambient_tenant_and_is_cleared_after()
    {
        var scope = new RecordingScope();

        var seenByActivity = await RunAsync(scope, tenantId: "tenant-a", workflow: nameof(TenantEchoWorkflow),
            client => client.ExecuteWorkflowAsync((TenantEchoWorkflow wf) => wf.RunAsync(), Options()));

        seenByActivity.Should().Be("tenant-a");
        scope.Calls.Should().Equal(
            "tenant:tenant-a",
            "user:tenant-a:temporal-system",
            "company:unfiltered",
            "company:cleared",
            "user:cleared",
            "tenant:cleared");
        scope.CurrentTenant.Should().BeNull();
    }

    [Fact]
    public async Task Each_run_carries_its_own_tenant()
    {
        var scope = new RecordingScope();

        var first = await RunAsync(scope, "tenant-a", nameof(TenantEchoWorkflow),
            client => client.ExecuteWorkflowAsync((TenantEchoWorkflow wf) => wf.RunAsync(), Options()));
        var second = await RunAsync(scope, "0193e2a0-0000-7000-8000-00000000b0b0", nameof(TenantEchoWorkflow),
            client => client.ExecuteWorkflowAsync((TenantEchoWorkflow wf) => wf.RunAsync(), Options()));

        first.Should().Be("tenant-a");
        second.Should().Be("0193e2a0-0000-7000-8000-00000000b0b0");
    }

    [Fact]
    public async Task A_workflow_the_client_interceptor_was_not_built_for_gets_no_header_and_cannot_run_activities()
    {
        var scope = new RecordingScope();

        var act = async () => await RunAsync(scope, "tenant-a", workflow: "SomeOtherWorkflow",
            client => client.ExecuteWorkflowAsync((TenantEchoWorkflow wf) => wf.RunAsync(), Options()));

        var failure = (await act.Should().ThrowAsync<WorkflowFailedException>()).Which;
        failure.InnerException.Should().BeOfType<ApplicationFailureException>()
            .Which.ErrorType.Should().Be("TenantScopeRequired");
        scope.Calls.Should().BeEmpty("no activity may run without a tenant");
    }

    [Theory]
    [InlineData("")]
    [InlineData("tenant with spaces")]
    public async Task The_client_refuses_to_start_a_workflow_for_an_unusable_tenant(string tenantId)
    {
        var scope = new RecordingScope();

        var act = async () => await RunAsync(scope, tenantId, nameof(TenantEchoWorkflow),
            client => client.ExecuteWorkflowAsync((TenantEchoWorkflow wf) => wf.RunAsync(), Options()));

        (await act.Should().ThrowAsync<ApplicationFailureException>()).Which.ErrorType.Should().Be("TenantScopeRequired");
        scope.Calls.Should().BeEmpty();
    }

    [Fact]
    public void The_sdk_worker_registration_does_not_add_the_header_interceptor_by_itself()
    {
        var source = File.ReadAllText(FindSource("TemporalWorkerServiceCollectionExtensions.cs"));

        source.Should().Contain(nameof(TenantContextActivityInterceptor));
        source.Should().NotContain(nameof(TenantHeaderWorkerInterceptor),
            "the header interceptor refuses every activity without a tenant header, so a worker opts in explicitly");
    }

    private static async Task<string?> RunAsync(
        RecordingScope scope, string tenantId, string workflow, Func<ITemporalClient, Task<string?>> run)
    {
        var services = new ServiceCollection()
            .AddSingleton(scope)
            .AddScoped<ITenantScopeSetter>(sp => sp.GetRequiredService<RecordingScope>())
            .AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<RecordingScope>())
            .AddScoped<ICompanyScopeSetter>(sp => sp.GetRequiredService<RecordingScope>())
            .BuildServiceProvider();

        await using var environment = await WorkflowEnvironment.StartTimeSkippingAsync();

        var clientOptions = (TemporalClientOptions)environment.Client.Options.Clone();
        clientOptions.Interceptors = [new TenantHeaderClientInterceptor(tenantId, workflow)];
        var tenantClient = new TemporalClient(environment.Client.Connection, clientOptions);

        var workerOptions = new TemporalWorkerOptions(TaskQueue)
        {
            Interceptors = [new TenantHeaderWorkerInterceptor(services.GetRequiredService<IServiceScopeFactory>())]
        };
        workerOptions.AddWorkflow<TenantEchoWorkflow>().AddAllActivities(new TenantEchoActivities(scope));
        using var worker = new TemporalWorker(environment.Client, workerOptions);

        return await worker.ExecuteAsync(() => run(tenantClient));
    }

    private static WorkflowOptions Options() => new($"tenant-echo-{Guid.NewGuid():N}", TaskQueue);

    private static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QFace.Sdk.sln")))
            directory = directory.Parent;

        return Directory.EnumerateFiles(
            Path.Combine(directory!.FullName, "src", "QFace.Sdk.Temporal"), fileName, SearchOption.AllDirectories).Single();
    }

    [Workflow]
    public sealed class TenantEchoWorkflow
    {
        [WorkflowRun]
        public Task<string?> RunAsync() =>
            Temporalio.Workflows.Workflow.ExecuteActivityAsync(
                (TenantEchoActivities a) => a.ReadAmbientTenant(),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }

    public sealed class TenantEchoActivities(RecordingScope scope)
    {
        [Activity]
        public string? ReadAmbientTenant() => scope.CurrentTenant;
    }

    public sealed class RecordingScope : ITenantScopeSetter, ITenantContextSetter, ICompanyScopeSetter
    {
        public List<string> Calls { get; } = [];
        public string? CurrentTenant { get; private set; }

        public void SetTenantScope(string? tenantId)
        {
            CurrentTenant = tenantId;
            Calls.Add($"tenant:{tenantId}");
        }

        public void ClearTenantScope()
        {
            CurrentTenant = null;
            Calls.Add("tenant:cleared");
        }

        public void SetTenantContext(string tenantId, string userEmail, string? userName = null, string? userId = null) =>
            Calls.Add($"user:{tenantId}:{userId}");

        public void ClearTenantContext() => Calls.Add("user:cleared");

        public void SetCompanyScope(string? activeCompanyId, IReadOnlyCollection<string>? allowedCompanyIds, bool filterActive) =>
            Calls.Add(filterActive ? "company:filtered" : "company:unfiltered");

        public void ClearCompanyScope() => Calls.Add("company:cleared");
    }
}
