using FluentAssertions;
using QFace.Sdk.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace QimErp.Shared.Common.Tests.Logging;

public sealed class HealthCheckLogFilterTests
{
    private static readonly string[] Paths = new LoggingOptions().HealthCheckPaths;

    private static LogEvent Event(LogEventLevel level, string property, string path) =>
        new(DateTimeOffset.UtcNow, level, null,
            new MessageTemplateParser().Parse("HTTP {Method} {" + property + "} responded"),
            [new LogEventProperty(property, new ScalarValue(path))]);

    [Theory]
    [InlineData("RequestPath", "/ready")]
    [InlineData("Path", "/health/ready")]
    [InlineData("PathAndQuery", "/live?x=1")]
    public void Drops_information_events_for_probe_paths(string property, string path) =>
        LoggingExtensions.IsHealthCheckNoise(Event(LogEventLevel.Information, property, path), Paths).Should().BeTrue();

    [Theory]
    [InlineData("/readyz")]
    [InlineData("/api/hr/employees")]
    [InlineData("/healthcheck-report")]
    public void Keeps_other_paths(string path) =>
        LoggingExtensions.IsHealthCheckNoise(Event(LogEventLevel.Information, "RequestPath", path), Paths).Should().BeFalse();

    [Fact]
    public void Keeps_probe_failures() =>
        LoggingExtensions.IsHealthCheckNoise(Event(LogEventLevel.Error, "RequestPath", "/ready"), Paths).Should().BeFalse();

    [Fact]
    public void Defaults_quiet_framework_request_chatter()
    {
        var overrides = new LoggingOptions().Overrides;

        overrides.Should().Contain("Microsoft.AspNetCore", LogEventLevel.Warning);
        overrides.Should().Contain("System.Net.Http.HttpClient", LogEventLevel.Warning);
        overrides.Should().Contain("Microsoft.AspNetCore.Authentication", LogEventLevel.Information);
        overrides.Should().Contain("Microsoft.AspNetCore.Authorization", LogEventLevel.Information);
        overrides.Should().Contain("Microsoft.AspNetCore.Cors", LogEventLevel.Information);
    }
}
