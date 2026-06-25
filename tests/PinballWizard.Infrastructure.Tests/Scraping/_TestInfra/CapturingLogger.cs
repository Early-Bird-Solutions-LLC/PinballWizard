using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Tests.Scraping._TestInfra;

/// <summary>
/// Minimal <see cref="ILogger"/> that collects every log entry so tests can
/// assert on log-level, message content, and any structured scope properties.
/// Used by extractor tests that need to verify invariant-#17 degradation
/// warnings (e.g. JSON-LD missing fallback).
/// </summary>
public sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}