using Prometheus;
using ShopEasy.Shared.Telemetry;

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

    // The payments of the synthetic probe's account (SyntheticTraffic) are left out: it buys something and
    // gets the money back every minute, which would drown the real figures of a quiet shop
    public static void RecordSucceeded(decimal amount, string? userEmail) => Record("succeeded", amount, userEmail);

    public static void RecordDeclined(decimal amount, string? userEmail) => Record("declined", amount, userEmail);

    public static void RecordRefunded(decimal amount, string? userEmail) => Record("refunded", amount, userEmail);

    private static void Record(string result, decimal amount, string? userEmail)
    {
        if (SyntheticTraffic.IsProbe(userEmail)) return;
        Payments.WithLabels(result).Inc();
        Amount.WithLabels(result).Inc((double)amount);
    }
}
