using Prometheus;

namespace ShopEasy.PaymentService.Metrics;

// What the payment provider answered, and what was given back, as the shop's money sees it
public static class PaymentMetrics
{
    private static readonly Counter Payments = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_payments_total",
            "Payments by result: succeeded and declined are the provider's answers, refunded counts payments given back",
            new CounterConfiguration
            {
                LabelNames = new[] { "result" } // "succeeded", "declined", "refunded"
            });

    private static readonly Counter Amount = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_payments_amount_total",
            "Sum of the amounts of those payments, in the shop's currency",
            new CounterConfiguration
            {
                LabelNames = new[] { "result" }
            });

    public static void RecordSucceeded(decimal amount) => Record("succeeded", amount);

    public static void RecordDeclined(decimal amount) => Record("declined", amount);

    public static void RecordRefunded(decimal amount) => Record("refunded", amount);

    private static void Record(string result, decimal amount)
    {
        Payments.WithLabels(result).Inc();
        Amount.WithLabels(result).Inc((double)amount);
    }
}
