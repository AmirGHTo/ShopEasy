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

    private static readonly Counter OrdersRejected = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_orders_rejected_total",
            "Order requests refused before anything was saved, by reason",
            new CounterConfiguration
            {
                LabelNames = new[] { "reason" } // see OrderRejection
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

    private static readonly Counter BackgroundLoopFailedPasses = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_background_loop_failed_passes_total",
            "Passes of a polling loop that failed or were cancelled for taking too long",
            new CounterConfiguration
            {
                LabelNames = new[] { "loop" } // saga_orchestrator, saga_timeout_service, outbox_delivery_service, dead_letter_monitor
            });

    // ── Gauges (can go up and down) ───────────────────────────────────────────
    private static readonly Gauge OutboxPendingMessages = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_outbox_pending_messages",
            "Current number of pending outbox messages");

    private static readonly Gauge ActiveSagas = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_sagas_active",
            "Current number of active (non-completed) sagas");

    private static readonly Gauge OldestActiveSagaAge = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_saga_oldest_active_age_seconds",
            "Age of the oldest saga that is still active (0 when there is none): rises when an order is stuck");

    private static readonly Gauge BackgroundLoopLastSuccess = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_background_loop_last_success_timestamp_seconds",
            "When a polling loop last completed a pass (Unix time, set when the loop is created too): " +
            "time() minus this is how long it has made no progress",
            new GaugeConfiguration
            {
                LabelNames = new[] { "loop" }
            });

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

    public static void RecordOrderRejected(string reason)
        => OrdersRejected.WithLabels(reason).Inc();

    // The order was paid for and its stock taken: the sale happened
    public static void RecordOrderConfirmed(decimal amount)
    {
        OrderOutcomes.WithLabels("confirmed", "completed").Inc();
        ConfirmedAmount.Inc((double)amount);
    }

    public static void RecordOrderCancelled(string reason)
        => OrderOutcomes.WithLabels("cancelled", reason).Inc();

    // The system could not finish the order and a person has to look at it
    public static void RecordOrderFailed(string reason)
        => OrderOutcomes.WithLabels("failed", reason).Inc();

    public static void RecordIdempotentReplay(string result)
        => IdempotentReplays.WithLabels(result).Inc();

    public static void RecordRetry(string target, string outcome)
        => DownstreamRetries.WithLabels(target, outcome).Inc();

    public static void RecordCircuitTransition(string target, string state)
        => CircuitTransitions.WithLabels(target, state).Inc();

    // Both series exist from the moment a loop is created, so a loop that never completes a pass still ages
    public static void RegisterLoop(string loop)
    {
        BackgroundLoopFailedPasses.WithLabels(loop);
        BackgroundLoopLastSuccess.WithLabels(loop).SetToCurrentTimeUtc();
    }

    public static void RecordLoopPassSucceeded(string loop)
        => BackgroundLoopLastSuccess.WithLabels(loop).SetToCurrentTimeUtc();

    public static void RecordLoopPassFailed(string loop)
        => BackgroundLoopFailedPasses.WithLabels(loop).Inc();

    public static void RecordOutboxDelivered()
        => OutboxMessagesDelivered.Inc();

    public static void RecordOutboxFailure()
        => OutboxDeliveryFailures.Inc();

    public static void SetOutboxPending(int count)
        => OutboxPendingMessages.Set(count);

    public static void SetSagasInFlight(int count, double oldestAgeSeconds)
    {
        ActiveSagas.Set(count);
        OldestActiveSagaAge.Set(oldestAgeSeconds);
    }

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

// The values of the "reason" label of shopeasy_order_outcomes_total for an order that failed
public static class FailReason
{
    public const string OrchestratorError = "orchestrator_error";
}

// The values of the "reason" label of shopeasy_orders_rejected_total: why an order request was
// refused before anything was saved
public static class OrderRejection
{
    public const string InvalidRequest = "invalid_request";
    public const string UnknownProduct = "unknown_product";
    public const string CatalogUnavailable = "catalog_unavailable";
    public const string InsufficientStock = "insufficient_stock";
    public const string InventoryUnreachable = "inventory_unreachable";
}
