using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using QFace.Sdk.ActorSystems;
using QimErp.Shared.Common.Activities.TenantActivity;
using QimErp.Shared.Common.Actors;
using QimErp.Shared.Common.Behaviours;
using QimErp.Shared.Common.ExceptionHandlers;
using QimErp.Shared.Common.Extensions;
using QimErp.Shared.Common.Logging;
using QimErp.Shared.Common.Middlewares;
using QimErp.Shared.Common.Services.Auth;
using QimErp.Shared.Common.Services.TenantActivity;
using Xunit;

namespace QimErp.Shared.Common.Tests.Logging;

public sealed class AnonymousRequestLoggingTests
{
    private const string Tenant = "tenant-a";
    private const string UserId = "user-7f3a91";
    private const string Email = "asker@tenant-a.test";
    private const string CorrelationId = "corr-55e1c0";
    private const string ClientIp = "203.0.113.44";
    private const string ConnectionId = "conn-0HN8Q";
    private const string EmployeeId = "6c1d3b52-0d2e-4f59-a3c4-9b7e8f1a2d3c";
    private const string SessionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Path = "/api/hr/feed/questions";

    private static readonly string[] Identity = [UserId, Email, CorrelationId, ClientIp, ConnectionId, EmployeeId, SessionId];

    private sealed record AnonymousAsk : IRequest<string>, IAnonymousRequest;

    private sealed record NamedAsk : IRequest<string>;

    [Fact]
    public async Task A_marked_request_logs_its_type_name_and_no_caller_identity()
    {
        var sink = new LogSink();
        var anonymous = new AnonymousRequestContext();
        var behaviour = new LoggingBehaviour<AnonymousAsk, string>(sink.Logger<LoggingBehaviour<AnonymousAsk, string>>(), Caller(), anonymous);
        var handler = sink.Logger("Handler");

        await behaviour.Handle(new AnonymousAsk(), () =>
        {
            handler.LogInformation("Handled");
            return Task.FromResult("ok");
        }, default);

        sink.Entries.Should().Contain(entry => entry.Message == "Starting feature AnonymousAsk");
        sink.Entries.Should().OnlyContain(entry => entry.Scopes.Contains("RequestType=AnonymousAsk"));
        ShouldCarryNoIdentity(sink);
        anonymous.IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public async Task An_unmarked_request_still_logs_the_user_and_correlation_ids()
    {
        var sink = new LogSink();
        var behaviour = new LoggingBehaviour<NamedAsk, string>(sink.Logger<LoggingBehaviour<NamedAsk, string>>(), Caller(), new AnonymousRequestContext());

        await behaviour.Handle(new NamedAsk(), () => Task.FromResult("ok"), default);

        sink.Entries.Should().ContainSingle().Which.Scopes.Should()
            .Contain($"UserId={UserId}").And.Contain($"CorrelationId={CorrelationId}").And.Contain("RequestType=NamedAsk");
    }

    [Fact]
    public async Task A_request_sent_inside_an_anonymous_request_logs_no_identity()
    {
        var sink = new LogSink();
        var anonymous = new AnonymousRequestContext();
        anonymous.MarkAnonymous();
        var behaviour = new LoggingBehaviour<NamedAsk, string>(sink.Logger<LoggingBehaviour<NamedAsk, string>>(), Caller(), anonymous);

        await behaviour.Handle(new NamedAsk(), () => Task.FromResult("ok"), default);

        sink.Entries.Should().ContainSingle().Which.Message.Should().Be("Starting feature NamedAsk");
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task The_user_scope_on_a_marked_endpoint_carries_no_user_id()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: true, out var anonymous);
        var inner = sink.Logger("Inner");
        var middleware = new UserLoggingScopeMiddleware(_ =>
        {
            inner.LogInformation("Inside");
            return Task.CompletedTask;
        }, sink.Factory);

        await middleware.InvokeAsync(context, Caller());

        sink.Entries.Should().ContainSingle().Which.Scopes.Should().Contain($"TenantId={Tenant}");
        ShouldCarryNoIdentity(sink);
        anonymous.IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public async Task The_user_scope_drops_the_user_id_once_a_marked_request_runs_under_an_unmarked_endpoint()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: false, out var anonymous);
        var inner = sink.Logger("Inner");
        var middleware = new UserLoggingScopeMiddleware(_ =>
        {
            inner.LogInformation("Before");
            anonymous.MarkAnonymous();
            inner.LogInformation("After");
            return Task.CompletedTask;
        }, sink.Factory);

        await middleware.InvokeAsync(context, Caller());

        sink.Entries.Single(entry => entry.Message == "Before").Scopes.Should().Contain($"UserId={UserId}");
        sink.Entries.Single(entry => entry.Message == "After").Text.Should().NotContain(UserId);
    }

    [Fact]
    public async Task Request_logging_on_a_marked_endpoint_writes_method_and_path_only()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: true, out _);
        var middleware = new RequestLoggingMiddleware(_ => Task.CompletedTask, sink.Logger<RequestLoggingMiddleware>(), VerboseRequestLogging());

        await middleware.InvokeAsync(context);

        sink.Entries.Should().ContainSingle().Which.Message.Should().Be($"HTTP request POST {Path}");
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task Request_logging_on_an_unmarked_endpoint_still_writes_the_client_ip_headers_and_body()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: false, out _);
        var middleware = new RequestLoggingMiddleware(_ => Task.CompletedTask, sink.Logger<RequestLoggingMiddleware>(), VerboseRequestLogging());

        await middleware.InvokeAsync(context);

        var all = string.Join("\n", sink.Entries.Select(entry => entry.Text));
        all.Should().Contain(ClientIp).And.Contain(ConnectionId).And.Contain(EmployeeId);
    }

    [Fact]
    public async Task Response_logging_after_a_marked_request_writes_status_method_and_path_only()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: false, out var anonymous);
        var middleware = new ResponseLoggingMiddleware(async http =>
        {
            anonymous.MarkAnonymous();
            http.Response.Headers["X-Correlation-Id"] = CorrelationId;
            await http.Response.WriteAsync($$"""{"employeeId":"{{EmployeeId}}"}""");
        }, sink.Logger<ResponseLoggingMiddleware>(), VerboseResponseLogging());

        await middleware.InvokeAsync(context);

        sink.Entries.Should().ContainSingle().Which.Message.Should().Be($"HTTP response 200 POST {Path}");
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task Response_logging_for_an_unmarked_request_still_carries_the_user_scope()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: false, out _);
        var middleware = new ResponseLoggingMiddleware(_ => Task.CompletedTask, sink.Logger<ResponseLoggingMiddleware>(), VerboseResponseLogging());

        await middleware.InvokeAsync(context);

        sink.Entries.Should().Contain(entry => entry.Scopes.Contains($"UserId={UserId}"));
    }

    [Fact]
    public async Task Request_and_response_logging_log_only_the_exception_type_for_a_marked_request()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: true, out _);
        var request = new RequestLoggingMiddleware(_ => throw IdentityCarryingException(), sink.Logger<RequestLoggingMiddleware>(), VerboseRequestLogging());
        var response = new ResponseLoggingMiddleware(_ => throw IdentityCarryingException(), sink.Logger<ResponseLoggingMiddleware>(), VerboseResponseLogging());

        await Assert.ThrowsAsync<InvalidOperationException>(() => request.InvokeAsync(context));
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.InvokeAsync(context));

        sink.Entries.Where(entry => entry.Level == LogLevel.Error).Should().HaveCount(2)
            .And.OnlyContain(entry => entry.Exception!.Contains(typeof(InvalidOperationException).FullName!));
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task Request_logging_still_attaches_the_exception_for_an_unmarked_request()
    {
        var sink = new LogSink();
        var context = Http(markedEndpoint: false, out _);
        var request = new RequestLoggingMiddleware(_ => throw IdentityCarryingException(), sink.Logger<RequestLoggingMiddleware>(), VerboseRequestLogging());

        await Assert.ThrowsAsync<InvalidOperationException>(() => request.InvokeAsync(context));

        sink.Entries.Single(entry => entry.Level == LogLevel.Error).Exception.Should().Contain(EmployeeId);
    }

    [Fact]
    public async Task The_exception_handler_logs_only_the_exception_type_for_a_marked_endpoint()
    {
        var sink = new LogSink();
        var context = ExceptionContext(markedEndpoint: true, IdentityCarryingException());

        var handled = await new GlobalExceptionHandler(sink.Logger<GlobalExceptionHandler>())
            .TryHandleAsync(context, IdentityCarryingException(), default);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        sink.Entries.Should().ContainSingle().Which.Exception.Should().Contain(typeof(InvalidOperationException).FullName!);
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task The_exception_handler_leaves_out_validation_messages_for_a_marked_request()
    {
        var sink = new LogSink();
        var context = ExceptionContext(markedEndpoint: false, null);
        AnonymousRequests.ContextOf(context)!.MarkAnonymous();
        var validation = new ValidationException([new ValidationFailure("Question", $"{Email} is not allowed here")]);

        await new GlobalExceptionHandler(sink.Logger<GlobalExceptionHandler>()).TryHandleAsync(context, validation, default);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        sink.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        ShouldCarryNoIdentity(sink);
    }

    [Fact]
    public async Task The_exception_handler_still_logs_the_exception_for_an_unmarked_endpoint()
    {
        var sink = new LogSink();
        var context = ExceptionContext(markedEndpoint: false, IdentityCarryingException());

        await new GlobalExceptionHandler(sink.Logger<GlobalExceptionHandler>()).TryHandleAsync(context, IdentityCarryingException(), default);

        sink.Entries.Should().ContainSingle().Which.Exception.Should().Contain(EmployeeId);
    }

    [Fact]
    public async Task Tracing_records_only_the_exception_type_for_a_marked_request()
    {
        var sink = new LogSink();
        var anonymous = new AnonymousRequestContext();
        anonymous.MarkAnonymous();
        var activities = new List<Activity>();
        using var listener = Listen(activities);
        var behaviour = new TracingBehaviour<NamedAsk, string>(sink.Logger<TracingBehaviour<NamedAsk, string>>(), anonymous);

        await Assert.ThrowsAsync<InvalidOperationException>(() => behaviour.Handle(new NamedAsk(), () => throw IdentityCarryingException(), default));

        ShouldCarryNoIdentity(sink);
        var activity = activities.Should().ContainSingle().Subject;
        activity.Tags.Select(tag => tag.Value).Should().NotContain(value => value != null && value.Contains(EmployeeId));
        (activity.StatusDescription ?? string.Empty).Should().NotContain(EmployeeId);
    }

    [Fact]
    public async Task Tracing_still_records_the_exception_message_for_an_unmarked_request()
    {
        var sink = new LogSink();
        var activities = new List<Activity>();
        using var listener = Listen(activities);
        var behaviour = new TracingBehaviour<NamedAsk, string>(sink.Logger<TracingBehaviour<NamedAsk, string>>(), new AnonymousRequestContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() => behaviour.Handle(new NamedAsk(), () => throw IdentityCarryingException(), default));

        activities.Should().ContainSingle().Which.GetTagItem("exception.message").Should().Be(IdentityCarryingException().Message);
    }

    [Fact]
    public void A_tenant_activity_recorded_by_a_marked_request_carries_no_ip_agent_session_or_timestamped_id()
    {
        var context = Http(markedEndpoint: false, out var anonymous);
        anonymous.MarkAnonymous();

        var recorded = RecordActivity(context);

        recorded.MetadataJson.Should().Be("""{"questionId":"q-1"}""");
        Guid.Parse(recorded.CorrelationId!).Version.Should().Be(4);
        recorded.MetadataJson!.Should().NotContain(ClientIp).And.NotContain("AuditTestAgent").And.NotContain(SessionId);
    }

    [Fact]
    public void A_tenant_activity_recorded_by_an_unmarked_request_still_carries_the_request_context()
    {
        var context = Http(markedEndpoint: false, out _);

        var recorded = RecordActivity(context);

        recorded.MetadataJson.Should().Contain(ClientIp).And.Contain("AuditTestAgent").And.Contain(SessionId);
        Guid.Parse(recorded.CorrelationId!).Version.Should().Be(7);
    }

    [Fact]
    public async Task A_marked_endpoint_gets_a_random_correlation_id()
    {
        var marked = Http(markedEndpoint: true, out _);
        var unmarked = Http(markedEndpoint: false, out _);
        marked.Request.Headers.Remove("X-Correlation-Id");
        unmarked.Request.Headers.Remove("X-Correlation-Id");
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(marked);
        await middleware.InvokeAsync(unmarked);

        Guid.Parse((string)marked.Items[CorrelationIdMiddleware.HttpContextItemKey]!).Version.Should().Be(4);
        Guid.Parse((string)unmarked.Items[CorrelationIdMiddleware.HttpContextItemKey]!).Version.Should().Be(7);
    }

    [Fact]
    public void AsAnonymousRequest_adds_the_endpoint_metadata()
    {
        var builder = new ConventionBuilder();

        builder.AsAnonymousRequest();

        var endpoint = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(Path), 0);
        builder.Conventions.ForEach(convention => convention(endpoint));
        endpoint.Build().Metadata.GetMetadata<AnonymousRequestMetadata>().Should().BeSameAs(AnonymousRequestMetadata.Instance);
    }

    [Fact]
    public void RegisterMediatR_registers_one_anonymous_request_context_per_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Caller());
        services.RegisterMediatR([typeof(AnonymousRequestLoggingTests).Assembly]);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<AnonymousRequestContext>();

        scope.ServiceProvider.GetRequiredService<AnonymousRequestContext>().Should().BeSameAs(first);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(AnonymousRequestContext) && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    private static void ShouldCarryNoIdentity(LogSink sink)
    {
        sink.Entries.Should().NotBeEmpty();
        foreach (var entry in sink.Entries)
            foreach (var value in Identity)
                entry.Text.Should().NotContain(value, $"the {entry.Category} entry \"{entry.Message}\" must not identify the caller");
    }

    private static InvalidOperationException IdentityCarryingException() =>
        new($"duplicate key value violates unique constraint: Key (EmployeeId)=({EmployeeId}) for {Email}");

    private static ICurrentUserService Caller()
    {
        var caller = new Mock<ICurrentUserService>();
        caller.Setup(user => user.GetTenantId()).Returns(Tenant);
        caller.Setup(user => user.GetUserId()).Returns(UserId);
        caller.Setup(user => user.GetUserEmail()).Returns(Email);
        caller.Setup(user => user.GetUserName()).Returns("Kofi Asker");
        caller.Setup(user => user.GetCorrelationId()).Returns(CorrelationId);
        return caller.Object;
    }

    private static DefaultHttpContext Http(bool markedEndpoint, out AnonymousRequestContext anonymous)
    {
        var services = new ServiceCollection();
        services.AddScoped<AnonymousRequestContext>();
        services.AddSingleton(Caller());
        var scope = services.BuildServiceProvider().CreateScope();
        anonymous = scope.ServiceProvider.GetRequiredService<AnonymousRequestContext>();

        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = $"{ConnectionId}:00000002" };
        context.Request.Method = "POST";
        context.Request.Path = Path;
        context.Request.Headers["X-Forwarded-For"] = ClientIp;
        context.Request.Headers["X-Correlation-Id"] = CorrelationId;
        context.Request.Headers.UserAgent = "AuditTestAgent/1.0";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""{"question":"Why?","employeeId":"{{EmployeeId}}"}"""));
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        context.Connection.Id = ConnectionId;
        context.Items[SessionContextKeys.CurrentSessionId] = SessionId;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, UserId), new Claim(ClaimTypes.Email, Email)], "test"));
        context.SetEndpoint(Endpoint(markedEndpoint));
        return context;
    }

    private static DefaultHttpContext ExceptionContext(bool markedEndpoint, Exception? error)
    {
        var context = Http(markedEndpoint: false, out _);
        context.SetEndpoint(null);
        if (error is not null)
            context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
            {
                Error = error,
                Endpoint = Endpoint(markedEndpoint),
                Path = Path
            });
        return context;
    }

    private static Endpoint Endpoint(bool marked) =>
        new(_ => Task.CompletedTask,
            marked ? new EndpointMetadataCollection(AnonymousRequestMetadata.Instance) : EndpointMetadataCollection.Empty,
            "AskFeedQuestion");

    private static RequestLoggingOptions VerboseRequestLogging() =>
        new() { LogRequestBody = true, LogRequestHeaders = true, LogQueryString = true };

    private static ResponseLoggingOptions VerboseResponseLogging() =>
        new() { LogResponseBody = true, LogResponseHeaders = true };

    private static RecordTenantActivityRequest RecordActivity(HttpContext context)
    {
        RecordTenantActivityRequest? captured = null;
        var actors = new Mock<IActorService>();
        actors
            .Setup(actor => actor.Tell<TenantActivityPublisherActor>(It.IsAny<TenantActivityPublishMessage>(), It.IsAny<string>()))
            .Callback<object, string>((message, _) => captured = ((TenantActivityPublishMessage)message).Request);

        new TenantActivityRecorder(actors.Object, new HttpContextAccessor { HttpContext = context }).Record(new RecordTenantActivityRequest
        {
            TenantId = Tenant,
            Module = TenantActivityModules.Hr,
            ActivityType = HrActivityTypes.EmployeeCreated,
            Summary = "Question asked",
            ActorUserId = Guid.Empty,
            MetadataJson = """{"questionId":"q-1"}"""
        });

        return captured!;
    }

    private static ActivityListener Listen(List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ObservabilityTelemetry.MediatRActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == nameof(NamedAsk))
                    lock (activities)
                        activities.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class ConventionBuilder : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }
}
