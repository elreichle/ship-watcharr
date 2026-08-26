using Microsoft.Extensions.Logging;

namespace Ao3Tracker.Tests;

/// <summary>
/// A logger provider that keeps every record as its structured values, and never formats one.
///
/// Formatting is what a provider normally does, and this one declines on purpose. A template whose
/// placeholders outnumber its arguments throws when it is rendered, so a fixture that formatted
/// every message would fail tests over defects in lines they say nothing about. Reading the named
/// values is also the more exact assertion: "the warning names page 2" is a claim about what
/// <c>{Page}</c> was bound to, not about where a number lands in a rendered sentence.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records
    {
        get { lock (_records) return [.. _records]; }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose() { }

    private void Add(LogRecord record)
    {
        lock (_records) _records.Add(record);
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // formatter is deliberately not called — see the remarks on the provider.
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            provider.Add(new LogRecord(category, logLevel, values, exception));
        }
    }
}

/// <summary>
/// One captured line: which category logged it, at what level, and the named values it carried.
/// </summary>
internal sealed record LogRecord(
    string Category,
    LogLevel Level,
    IReadOnlyList<KeyValuePair<string, object?>> Values,
    Exception? Exception)
{
    /// <summary>The message template, as the logging framework records it under <c>{OriginalFormat}</c>.</summary>
    public string Template => Value("{OriginalFormat}")?.ToString() ?? "";

    /// <summary>What a named placeholder was bound to, or null if the template has no such name.</summary>
    public object? Value(string name) =>
        Values.FirstOrDefault(v => v.Key == name).Value;
}
