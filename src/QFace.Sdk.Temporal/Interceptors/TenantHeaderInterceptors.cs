using Microsoft.Extensions.DependencyInjection;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;

namespace QFace.Sdk.Temporal.Interceptors;

public sealed class TenantHeaderClientInterceptor(string tenantId, string workflowType) : IClientInterceptor
{
    public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor nextInterceptor)
        => new Outbound(nextInterceptor, tenantId, workflowType);

    private sealed class Outbound(ClientOutboundInterceptor next, string tenantId, string workflowType)
        : ClientOutboundInterceptor(next)
    {
        public override Task<WorkflowHandle<TWorkflow, TResult>> StartWorkflowAsync<TWorkflow, TResult>(
            StartWorkflowInput input)
            => base.StartWorkflowAsync<TWorkflow, TResult>(
                input.Workflow == workflowType
                    ? input with { Headers = TenantHeaders.Set(input.Headers, TenantHeaders.Encode(tenantId)) }
                    : input);
    }
}

public sealed class TenantHeaderWorkerInterceptor(IServiceScopeFactory scopeFactory) : IWorkerInterceptor
{
    public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor)
        => new WorkflowInbound(nextInterceptor);

    public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor)
        => new ActivityInbound(nextInterceptor, scopeFactory);

    private sealed class WorkflowInbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
    {
        public override void Init(WorkflowOutboundInterceptor outbound)
            => base.Init(new WorkflowOutbound(outbound));
    }

    private sealed class WorkflowOutbound(WorkflowOutboundInterceptor next) : WorkflowOutboundInterceptor(next)
    {
        public override Task<TResult> ScheduleActivityAsync<TResult>(ScheduleActivityInput input)
            => base.ScheduleActivityAsync<TResult>(input with
            {
                Headers = TenantHeaders.Set(input.Headers, TenantHeaders.Require(Workflow.Info.Headers))
            });

        public override Task<TResult> ScheduleLocalActivityAsync<TResult>(ScheduleLocalActivityInput input)
            => base.ScheduleLocalActivityAsync<TResult>(input with
            {
                Headers = TenantHeaders.Set(input.Headers, TenantHeaders.Require(Workflow.Info.Headers))
            });
    }

    private sealed class ActivityInbound(ActivityInboundInterceptor next, IServiceScopeFactory scopeFactory)
        : ActivityInboundInterceptor(next)
    {
        public override async Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
        {
            var tenantId = TenantHeaders.Decode(TenantHeaders.Require(input.Headers));
            using var scope = scopeFactory.CreateScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeSetter>();
            var user = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>();
            var company = scope.ServiceProvider.GetRequiredService<ICompanyScopeSetter>();
            try
            {
                tenant.SetTenantScope(tenantId);
                user.SetTenantContext(tenantId, "temporal-system", userId: "temporal-system");
                company.SetCompanyScope(null, null, filterActive: false);
                return await base.ExecuteActivityAsync(input);
            }
            finally
            {
                company.ClearCompanyScope();
                user.ClearTenantContext();
                tenant.ClearTenantScope();
            }
        }
    }
}

internal static class TenantHeaders
{
    internal const string Name = "qface-tenant-id";
    private static readonly IPayloadConverter Converter = DataConverter.Default.PayloadConverter;

    internal static Payload Encode(string tenantId)
        => IsValid(tenantId) ? Converter.ToPayload(tenantId) : throw Invalid();

    internal static string Decode(Payload payload)
    {
        string? tenantId;
        try
        {
            tenantId = Converter.ToValue(payload, typeof(string)) as string;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Invalid();
        }

        return IsValid(tenantId) ? tenantId! : throw Invalid();
    }

    internal static Payload Require(IReadOnlyDictionary<string, Payload>? headers)
        => headers is not null && headers.TryGetValue(Name, out var payload) ? payload : throw Invalid();

    internal static IDictionary<string, Payload> Set(IDictionary<string, Payload>? headers, Payload tenant)
    {
        var result = headers is null ? new Dictionary<string, Payload>() : new Dictionary<string, Payload>(headers);
        result[Name] = tenant;
        return result;
    }

    private static bool IsValid(string? tenantId)
        => tenantId is { Length: > 0 and <= 128 } && tenantId.All(ch => !char.IsWhiteSpace(ch) && !char.IsControl(ch));

    private static ApplicationFailureException Invalid()
        => new("A valid tenant header is required.", errorType: "TenantScopeRequired", nonRetryable: true);
}
