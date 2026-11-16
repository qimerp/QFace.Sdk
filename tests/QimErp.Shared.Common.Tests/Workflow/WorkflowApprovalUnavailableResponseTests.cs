using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using QimErp.Shared.Common.ExceptionHandlers;
using QimErp.Shared.Common.Services.Workflow;
using Xunit;

namespace QimErp.Shared.Common.Tests.Workflow;

public class WorkflowApprovalUnavailableResponseTests
{
    [Fact]
    public async Task UnavailableApprovalState_MapsToRetryable503()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var exception = new WorkflowApprovalStateUnavailableException("tenant-1", "HR", "Employee", "CREATE");

        var handled = await new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, default);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrWhiteSpace();

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("detail").GetString().Should().Be(exception.Message);
        exception.Message.Should().Contain("Employee").And.Contain("not saved");
    }

    [Fact]
    public async Task PresetCodeMismatch_MapsToPermanent409_WithoutRetryAfterOrTenantData()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var exception = new WorkflowPresetCodeMismatchException("tenant-1", "HR", "Employee", "OLD-WF", "CREATE-WF");

        var handled = await new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, default);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        var detail = body.RootElement.GetProperty("detail").GetString();
        detail.Should().Be(exception.Message).And.Contain("Employee");
        detail.Should().NotContain("tenant-1").And.NotContain("OLD-WF").And.NotContain("CREATE-WF");
    }
}
