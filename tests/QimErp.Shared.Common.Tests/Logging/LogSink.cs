using Microsoft.Extensions.Logging;

namespace QimErp.Shared.Common.Tests.Logging;

internal sealed record LogEntry(LogLevel Level, string Category, string Message, string State, string Scopes, string? Exception)
{
    public string Text => $"{Message}|{State}|{Scopes}|{Exception}";
}

/// <summary>Reads scope values at log time, as Serilog and the JSON console do.</summary>
internal sealed class LogSink : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public LogSink()
    {
        Factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this));
    }

    public ILoggerFactory Factory { get; }

    public List<LogEntry> Entries { get; } = [];

    public ILogger<T> Logger<T>() => Factory.CreateLogger<T>();

    public ILogger Logger(string category) => Factory.CreateLogger(category);

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static string Describe(object? state) =>
        state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(",", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
            : state?.ToString() ?? string.Empty;

    private sealed class SinkLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => sink._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            sink._scopes.ForEachScope((scope, list) => list.Add(Describe(scope)), scopes);
            lock (sink.Entries)
                sink.Entries.Add(new LogEntry(logLevel, category, formatter(state, exception), Describe(state),
                    string.Join(";", scopes), exception?.ToString()));
        }
    }
}
