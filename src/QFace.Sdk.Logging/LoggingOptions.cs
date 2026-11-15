using Serilog.Events;

namespace QFace.Sdk.Logging;

/// <summary>
/// Configuration options for QFace logging with Graylog
/// </summary>
public class LoggingOptions
{
    /// <summary>
    /// Graylog server URL
    /// </summary>
    public string Url { get; set; } = "localhost";
    public string Using { get; set; } = "Graylog";
    
    /// <summary>
    /// Graylog UDP port
    /// </summary>
    public int Port { get; set; } = 12201;
    
    /// <summary>
    /// Application name for logging identification
    /// </summary>
    public string Facility { get; set; } = "QFace.Sdk.Logging";
    
    /// <summary>
    /// Minimum level of logs to capture
    /// </summary>
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;
    
    /// <summary>
    /// Whether to include console logging alongside Graylog
    /// </summary>
    public bool IncludeConsole { get; set; } = true;
    
    /// <summary>
    /// Transport type for Graylog (Udp or Http)
    /// Defaults to Udp for better performance
    /// </summary>
    public string TransportType { get; set; } = "Udp";

    /// <summary>
    /// Per-category minimum levels; a key set in configuration replaces the default for that category.
    /// </summary>
    public Dictionary<string, LogEventLevel> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AspNetCore"] = LogEventLevel.Warning,
        ["System.Net.Http.HttpClient"] = LogEventLevel.Warning,
        ["Polly"] = LogEventLevel.Warning,
    };

    /// <summary>
    /// Request paths whose below-Warning events are dropped, so uptime probes do not flood the logs.
    /// </summary>
    public string[] HealthCheckPaths { get; set; } = { "/ready", "/live", "/alive", "/health" };
}