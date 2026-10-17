using Prometheus;

namespace ShopEasy.OrderService.Metrics;

// Custom business metrics for OrderService.
// These go beyond HTTP metrics to track business-level KPIs.
public static class OrderMetrics
{
    // ── Counters (always increasing) ─────────────────────────────────────────
    private static readonly Counter OrdersPlaced = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_orders_placed_total",
            "Orders accepted at the door (saved as Pending). What becomes of them is in shopeasy_order_outcomes_total",
            new CounterConfiguration
            {
                LabelNames = new[] { "status" } // "accepted"
            });

    private static readonly Counter OrderOutcomes = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_order_outcomes_total",
            "How accepted orders ended, by outcome and reason",
            new CounterConfiguration
            {
                LabelNames = new[] { "outcome", "reason" } // "confirmed" / "completed"
            });

    private static readonly Counter ConfirmedAmount = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_orders_confirmed_amount_total",
            "Sum of the total amounts of confirmed orders, in the shop's currency");

    private static readonly Counter IdempotentReplays = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_orders_idempotent_replays_total",
            "Order requests answered from a stored response because the Idempotency-Key was already used",
            new CounterConfiguration
            {
                LabelNames = new[] { "result" } // "replayed", "key_reused_with_different_request"
            });

    private static readonly Counter DownstreamRetries = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_downstream_retries_total",
            "Retries of calls to another service, and retries refused because the retry budget was used up",
            new CounterConfiguration
            {
                LabelNames = new[] { "target", "outcome" } // outcome: "retried", "budget_exhausted"
            });

    private static readonly Counter CircuitTransitions = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_downstream_circuit_transitions_total",
            "Circuit breaker state changes for calls to another service",
            new CounterConfiguration
            {
                LabelNames = new[] { "target", "state" } // state: "open", "half_open", "closed"
            });

    private static readonly Counter OutboxMessagesDelivered = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_outbox_messages_delivered_total",
            "Total outbox messages successfully delivered to RabbitMQ");

    private static readonly Counter OutboxDeliveryFailures = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_outbox_delivery_failures_total",
            "Total outbox delivery failures");

    // ── Gauges (can go up and down) ───────────────────────────────────────────
    private static readonly Gauge OutboxPendingMessages = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_outbox_pending_messages",
            "Current number of pending outbox messages");

    private static readonly Gauge ActiveSagas = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_sagas_active",
            "Current number of active (non-completed) sagas");

    // ── Histograms (distribution of values) ───────────────────────────────────
    private static readonly Histogram SagaDuration = Prometheus.Metrics
        .CreateHistogram(
            "shopeasy_saga_duration_seconds",
            "Time taken for saga to complete",
            new HistogramConfiguration
            {
                LabelNames = new[] { "result" }, // "completed", "cancelled", "failed"
                Buckets = new[] { .1, .5, 1, 2, 5, 10, 30 }
            });

    private static readonly Histogram OrderAmount = Prometheus.Metrics
        .CreateHistogram(
            "shopeasy_order_amount_dollars",
            "Distribution of order amounts",
            new HistogramConfiguration
            {
                Buckets = new[] { 10.0, 50, 100, 250, 500, 1000, 5000 }
            });

    // ── Public methods ────────────────────────────────────────────────────────

    public static void RecordOrderPlaced(decimal amount)
    {
        OrdersPlaced.WithLabels("accepted").Inc();
        OrderAmount.Observe((double)amount);
    }

    // The order was paid for and its stock taken: the sale happened
    public static void RecordOrderConfirmed(decimal amount)
    {
        OrderOutcomes.WithLabels("confirmed", "completed").Inc();
        ConfirmedAmount.Inc((double)amount);
    }

    public static void RecordOrderCancelled(string reason)
        => OrderOutcomes.WithLabels("cancelled", reason).Inc();

    public static void RecordIdempotentReplay(string result)
        => IdempotentReplays.WithLabels(result).Inc();

    public static void RecordRetry(string target, string outcome)
        => DownstreamRetries.WithLabels(target, outcome).Inc();

    public static void RecordCircuitTransition(string target, string state)
        => CircuitTransitions.WithLabels(target, state).Inc();

    public static void RecordOutboxDelivered()
        => OutboxMessagesDelivered.Inc();

    public static void RecordOutboxFailure()
        => OutboxDeliveryFailures.Inc();

    public static void SetOutboxPending(int count)
        => OutboxPendingMessages.Set(count);

    public static void SetActiveSagas(int count)
        => ActiveSagas.Set(count);

    public static void RecordSagaCompleted(TimeSpan duration)
        => SagaDuration.WithLabels("completed").Observe(duration.TotalSeconds);

    public static void RecordSagaCancelled(TimeSpan duration)
        => SagaDuration.WithLabels("cancelled").Observe(duration.TotalSeconds);

    public static void RecordSagaFailed()
        => SagaDuration.WithLabels("failed").Observe(0);
}

// The values of the "reason" label of shopeasy_order_outcomes_total for a cancelled order
public static class CancelReason
{
    public const string StockUnavailable = "stock_unavailable";
    public const string PaymentDeclined = "payment_declined";
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Timeout = "timeout";
    public const string InventoryCommitFailed = "inventory_commit_failed";
    public const string ReleaseNotConfirmed = "release_not_confirmed";
}
