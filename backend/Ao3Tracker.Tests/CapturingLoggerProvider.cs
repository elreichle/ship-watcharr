using Microsoft.Extensions.Logging;

namespace Ao3Tracker.Tests;

/// <summary>
/// A logger provider that keeps every record as its structured values, and formats one only when
/// asked to.
///
/// Formatting is what a provider normally does, and by default this one declines. Reading the named
/// values is the more exact assertion: "the warning names page 2" is a claim about what
/// <c>{Page}</c> was bound to, not about where a number lands in a rendered sentence.
///
/// <c>renderMessages</c> turns rendering on, and it is not a convenience — it is the
/// only way a test can see a broken template at all. A template whose placeholders outnumber its
/// arguments is rewritten by <c>LogValuesFormatter</c> into a <c>string.Format</c> call with too
/// few values, so it throws the moment any provider renders it, and <c>Logger.Log</c> rethrows that
/// as an <see cref="AggregateException"/> through whatever line emitted it. Under a provider that
/// never renders, that defect is invisible: the app throws in production and every test passes. So
/// a class that renders is asserting something about every template its code under test emits, not
/// only the ones it names — which is why the flag belongs to the fixture and not to a single test.
/// </summary>
internal sealed class CapturingLoggerProvider(bool renderMessages = false) : ILoggerProvider
{
    private readonly List<LogRecord> _records = [];

    private bool RenderMessages { get; } = renderMessages;

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
            // Called first, and never guarded: a template that cannot render is a defect this
            // fixture exists to surface, so it must reach the test as the failure it is.
            var message = provider.RenderMessages ? formatter(state, exception) : null;
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            provider.Add(new LogRecord(category, logLevel, values, exception, message));
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
    Exception? Exception,
    string? Message = null)
{
    /// <summary>The message template, as the logging framework records it under <c>{OriginalFormat}</c>.</summary>
    public string Template => Value("{OriginalFormat}")?.ToString() ?? "";

    /// <summary>What a named placeholder was bound to, or null if the template has no such name.</summary>
    public object? Value(string name) =>
        Values.FirstOrDefault(v => v.Key == name).Value;
}
