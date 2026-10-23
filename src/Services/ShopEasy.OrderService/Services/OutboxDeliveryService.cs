using global::ShopEasy.OrderService.Data;
using global::ShopEasy.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopEasy.OrderService.Metrics;
using ShopEasy.Shared.Messaging;

namespace ShopEasy.OrderService.Services;

// Background worker that runs every 5 seconds.
// Picks up pending outbox entries and publishes them to RabbitMQ.
// If RabbitMQ is down, it retries next cycle — the order is never lost.
//
// Delivery only starts while the message bus is ready (IBrokerReadiness). That keeps an outage
// or a start-up race — the broker unreachable, or the consuming queue not declared yet — from
// being counted as failed delivery attempts against entries that were never actually tried.

public class OutboxDeliveryService(
    IServiceScopeFactory scopeFactory,
    IBrokerReadiness brokerReadiness,
    ILogger<OutboxDeliveryService> logger,
    IOptions<BackgroundLoopOptions>? loops = null) : BackgroundService
{
    private readonly GuardedPass _guard = new(logger, "Outbox delivery service", loops);
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IBrokerReadiness _brokerReadiness = brokerReadiness;
    private readonly ILogger<OutboxDeliveryService> _logger = logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private bool? _lastReady;
    private const int MaxRetries = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox delivery service started. Polling every {Interval}s",
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _guard.RunAsync(DeliverPendingMessages, stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    // One delivery cycle. Internal so tests can run a single cycle without the 5-second timer.
    internal async Task DeliverPendingMessages(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        // The bus itself, not IPublishEndpoint: the MassTransit outbox (Program.cs) replaces the
        // scoped IPublishEndpoint with one that stores the message in the database, and this
        // service is already reading its own outbox table
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IBus>();

        var pendingCount = await db.OutboxEntries
            .CountAsync(e => e.DeliveredAt == null && e.RetryCount < MaxRetries, ct);
        OrderMetrics.SetOutboxPending(pendingCount);

        if (pendingCount == 0) return;
        if (!await BrokerIsReady(ct)) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var pending = await db.OutboxEntries
                .FromSqlRaw("""
                SELECT * FROM "OutboxEntries"
                WHERE "DeliveredAt" IS NULL
                AND "RetryCount" < {0}
                ORDER BY "CreatedAt"
                LIMIT 20
                FOR UPDATE SKIP LOCKED
                """, MaxRetries)
                .ToListAsync(ct);

            if (!pending.Any())
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            foreach (var entry in pending)
            {
                try
                {
                    // The worker has no HTTP request: restore the id of the request that placed
                    // the order, so the message (and the logs below) carry it
                    using (CorrelationContext.Push(entry.CorrelationId))
                        await PublishEntry(entry, publishEndpoint, ct);
                    entry.DeliveredAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    OrderMetrics.RecordOutboxDelivered();

                    _logger.LogInformation(
                        "Outbox: delivered {EventType} {EntryId}",
                        entry.EventType, entry.Id);

                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                }
                catch (Exception ex)
                {
                    entry.RetryCount++;

                    if (entry.RetryCount >= MaxRetries)
                    {
                        _logger.LogError(
                            "Outbox: entry {EntryId} ({EventType}) exceeded max retries " +
                            "({MaxRetries}). Moving to dead letter state.",
                            entry.Id, entry.EventType, MaxRetries);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Outbox: failed to deliver {EventType} " +
                            "(attempt {Attempt}/{Max}): {Error}",
                            entry.EventType, entry.RetryCount, MaxRetries, ex.Message);
                    }

                    await db.SaveChangesAsync(ct);
                    OrderMetrics.RecordOutboxFailure();

                    // COMMIT, do not roll back: the retry count must be stored (otherwise a
                    // poison entry would fail forever, never reach the dead-letter state and
                    // block every entry behind it), and the entries of this batch that were
                    // already published must stay marked as delivered. The rest of the batch
                    // waits for the next cycle.
                    await transaction.CommitAsync(ct);
                    return;
                }
            }

            await transaction.CommitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Outbox: unexpected error in delivery batch");
        }
    }

    private async Task<bool> BrokerIsReady(CancellationToken ct)
    {
        bool ready;
        try
        {
            ready = await _brokerReadiness.IsReadyAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox: could not determine message bus readiness");
            ready = false;
        }

        // Log only when the state changes — not every 5 seconds while the broker is down
        if (_lastReady != ready)
        {
            if (ready)
                _logger.LogInformation("Outbox: message bus is ready — delivery enabled");
            else
                _logger.LogWarning("Outbox: message bus is not ready — delivery paused, entries stay pending");
            _lastReady = ready;
        }

        return ready;
    }

    private static async Task PublishEntry(OutboxEntry entry,IPublishEndpoint publishEndpoint,CancellationToken ct)
    {
        switch (entry.EventType)
        {
            case nameof(OrderConfirmedEvent):
                var evt = System.Text.Json.JsonSerializer
                    .Deserialize<OrderConfirmedEvent>(entry.Payload,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        })!;
                // Always under the entry's own id. If the message is published but marking the entry
                // delivered then fails, the next cycle publishes it again; with the same MessageId
                // the receiving inbox recognises the copy instead of treating it as a new event.
                await publishEndpoint.Publish(evt, context => context.MessageId = entry.Id, ct);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown event type: {entry.EventType}");
        }
    }
}