using Microsoft.Extensions.Logging;

namespace ShopEasy.UnitTests.Support;

// Collects log entries so tests can assert what an operator would see in the logs.
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }

    public IReadOnlyList<string> Messages(LogLevel level)
    {
        lock (Entries) return Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
    }
}
