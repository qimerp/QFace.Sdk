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
    /// Per-category minimum levels; configuration adds keys or replaces a default's level, it cannot remove one.
    /// </summary>
    public Dictionary<string, LogEventLevel> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AspNetCore"] = LogEventLevel.Warning,
        ["Microsoft.AspNetCore.Authentication"] = LogEventLevel.Information,
        ["Microsoft.AspNetCore.Authorization"] = LogEventLevel.Information,
        ["Microsoft.AspNetCore.Cors"] = LogEventLevel.Information,
        ["System.Net.Http.HttpClient"] = LogEventLevel.Warning,
        ["Polly"] = LogEventLevel.Warning,
    };

    /// <summary>
    /// Below-Warning events for these request paths (and their child paths) are dropped; configuration appends to the defaults.
    /// </summary>
    public string[] HealthCheckPaths { get; set; } = { "/ready", "/live", "/alive", "/health" };
}