using Serilog.Context;

namespace ShopEasy.Shared.Messaging;

// The correlation id of the work currently being done: the same id on the HTTP request, in the
// log lines of every service that touches it, and in the header of every message it causes.
// In Seq, `CorrelationId = 'a1b2c3d4'` shows one customer action across the whole system.
//
// It is ambient (AsyncLocal) so that code which publishes a message does not have to pass it
// along: the publish filter stamps the current id onto the outgoing message, the consume filter
// restores it from the incoming message.
public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-Id";
    public const string LogProperty = "CorrelationId";

    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current => CurrentId.Value;

    // Makes `id` the current correlation id, and adds it to every log line, until disposed.
    // A null or empty id changes nothing (there is nothing to correlate with).
    public static IDisposable Push(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NoScope.Instance;

        var previous = CurrentId.Value;
        CurrentId.Value = id;
        var logScope = LogContext.PushProperty(LogProperty, id);
        return new Scope(previous, logScope);
    }

    // A new short id, same shape the HTTP middleware has always generated
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private sealed class Scope(string? previous, IDisposable logScope) : IDisposable
    {
        public void Dispose()
        {
            logScope.Dispose();
            CurrentId.Value = previous;
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}
