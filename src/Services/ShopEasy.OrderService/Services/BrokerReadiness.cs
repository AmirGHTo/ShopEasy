using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopEasy.OrderService.Services;

// "Can this service publish to RabbitMQ right now?" — asked by the outbox delivery worker
// before it sends a batch, so an outage or a start-up race does not burn the retry budget of
// entries that were never actually attempted.
public interface IBrokerReadiness
{
    Task<bool> IsReadyAsync(CancellationToken ct = default);
}

// Answers from MassTransit's own bus health check ("masstransit-bus", tagged "masstransit").
// It reports healthy only when the bus is started, the RabbitMQ connection is up and every
// receive endpoint — including the one that consumes OrderConfirmedEvent — has declared its
// queue and bindings. That last part is what matters at start-up: an event published to an
// exchange that has no queue bound yet would be dropped by the broker.
//
// Nothing here talks to the RabbitMQ management API (port 15672); the service only needs the
// AMQP port it already uses for messaging.
public sealed class MassTransitBrokerReadiness(
    HealthCheckService healthChecks,
    ILogger<MassTransitBrokerReadiness> logger) : IBrokerReadiness
{
    public const string HealthCheckTag = "masstransit";

    private bool _warnedAboutMissingCheck;

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains(HealthCheckTag), ct);

        if (report.Entries.Count == 0)
        {
            // Should not happen: AddMassTransit registers the check. Fail closed — publishing
            // without knowing the bus state is exactly what this gate exists to prevent.
            if (!_warnedAboutMissingCheck)
            {
                _warnedAboutMissingCheck = true;
                logger.LogError(
                    "No MassTransit health check is registered, so broker readiness cannot be " +
                    "determined. Outbox delivery is paused.");
            }
            return false;
        }

        return report.Status == HealthStatus.Healthy;
    }
}
