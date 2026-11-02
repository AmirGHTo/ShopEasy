using System.Diagnostics;
using ShopEasy.OrderService.Data;

namespace ShopEasy.OrderService.Tracing;

// The spans the Order service writes itself, and the way a trace is carried over the places where
// work waits in a database table.
//
// Message consumers and HTTP requests continue a trace by themselves (the trace context travels in the
// HTTP header or the message header). Three things in the order's life do not, because they happen
// later, in a background loop, with nothing in hand but a row:
//
//   1. the outbox entry that holds the OrderConfirmedEvent until it is delivered,
//   2. the saga the orchestrator picks up on its next look (every 3 s),
//   3. the saga the timeout service finds stuck.
//
// So the trace context of the work that wrote the row is stored on the row (OutboxEntry.TraceContext,
// OrderSagaState.TraceContext), and the loop starts its span inside that trace. The request that
// placed the order, the saga that starts, every command and event down to the confirmation then are
// one trace, instead of the three they would otherwise be, and "order.id" finds it in Jaeger.
public static class OrderSpans
{
    // Registered with OpenTelemetry in Program.cs (AddSource)
    public const string SourceName = "ShopEasy.Order";

    public static readonly ActivitySource Source = new(SourceName);

    // A traceparent is 55 characters; the column has room for more in case the format grows
    public const int MaxTraceContextLength = 128;

    // The trace context of the span that is running, to be stored on a row: the W3C "traceparent"
    // (version-traceid-spanid-flags). Null when there is no span, e.g. in a test.
    public static string? CurrentContext() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } span ? span.Id : null;

    // A span for work on a saga, inside the trace its row remembers (a new trace when it remembers
    // none, as for a saga that is older than this column). Null when nothing listens, which is how
    // the unit tests and a service with its tracing off run.
    public static Activity? StartForSaga(string name, OrderSagaState saga)
    {
        var span = Start(name, saga.TraceContext);
        Tag(span, saga);
        return span;
    }

    // A span for work on an outbox entry (delivering the event), inside the trace of the request
    // that placed the order
    public static Activity? StartForOutboxEntry(string name, OutboxEntry entry) => Start(name, entry.TraceContext);

    // What one searches for in Jaeger: every trace of one order, whichever service wrote the span.
    // (order.id is also put on the span of every message the order's saga sends, see
    // CorrelationConsumeFilter in ShopEasy.Shared.)
    public static void TagOrder(Guid orderId) => Activity.Current?.SetTag("order.id", orderId.ToString());

    // Where the saga is: set when the span starts and again when the work is done
    public static void TagStatus(Activity? span, SagaStatus status) => span?.SetTag("saga.status", status.ToString());

    public static void Fail(Activity? span, Exception error) => span?.SetStatus(ActivityStatusCode.Error, error.Message);

    private static Activity? Start(string name, string? traceContext) =>
        traceContext is not null
        && ActivityContext.TryParse(traceContext, null, isRemote: true, out var parent)
            ? Source.StartActivity(name, ActivityKind.Internal, parent)
            : Source.StartActivity(name, ActivityKind.Internal);

    private static void Tag(Activity? span, OrderSagaState saga)
    {
        if (span is null) return;
        span.SetTag("order.id", saga.OrderId.ToString());
        span.SetTag("saga.id", saga.Id.ToString());
        TagStatus(span, saga.Status);
    }
}
