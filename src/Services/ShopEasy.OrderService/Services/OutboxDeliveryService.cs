using global::ShopEasy.OrderService.Data;
using global::ShopEasy.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Metrics;
using System.Net.Http;

namespace ShopEasy.OrderService.Services;

// Background worker that runs every 5 seconds.
// Picks up pending outbox entries and publishes them to RabbitMQ.
// If RabbitMQ is down, it retries next cycle — the order is never lost.

public class OutboxDeliveryService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<OutboxDeliveryService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<OutboxDeliveryService> _logger = logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IConfiguration _config = config;
    private const int MaxRetries = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox delivery service started. Polling every {Interval}s",
            _interval.TotalSeconds);

        // Wait for app to fully start before polling
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await DeliverPendingMessages(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task DeliverPendingMessages(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var pendingCount = await db.OutboxEntries
            .CountAsync(e => e.DeliveredAt == null && e.RetryCount < MaxRetries, ct);
        OrderMetrics.SetOutboxPending(pendingCount);

        if (pendingCount == 0) return;
        if (!await ConsumersAreReady(ct)) return;

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
                    await transaction.RollbackAsync(ct);
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

    private async Task<bool> ConsumersAreReady(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var host = _config["RabbitMQ:Host"] ?? "rabbitmq";
            var user = _config["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username is not configured.");
            var pass = _config["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password is not configured.");
            var auth = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{user}:{pass}"));

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            // Check order-saga-starter has a consumer
            var res = await client.GetAsync(
                $"http://{host}:15672/api/queues/%2F/order-saga-starter", ct);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Outbox: order-saga-starter queue not found");
                return false;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var consumers = doc.RootElement.GetProperty("consumers").GetInt32();

            if (consumers == 0)
            {
                _logger.LogWarning(
                    "Outbox: order-saga-starter has no consumers, skipping delivery");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Outbox: cannot reach RabbitMQ management API: {Error}", ex.Message);
            return false;
        }
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
                await publishEndpoint.Publish(evt, ct);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown event type: {entry.EventType}");
        }
    }
}