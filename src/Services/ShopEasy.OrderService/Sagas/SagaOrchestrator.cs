using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;
using System.Text.Json;

namespace ShopEasy.OrderService.Sagas;

// This background service is our custom Saga orchestrator.
// It polls pending sagas and drives them forward step by step.
// Each step publishes a command and waits for a response event.
public class SagaOrchestrator : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SagaOrchestrator> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);

    public SagaOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<SagaOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Saga orchestrator started");

        while (!ct.IsCancellationRequested)
        {
            await ProcessPendingSagas(ct);
            await Task.Delay(_pollInterval, ct);
        }
    }

    // internal: a test runs one polling pass without the 3-second loop
    internal async Task ProcessPendingSagas(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OrderDbContext>();
        var publishEndpoint = scope.ServiceProvider
            .GetRequiredService<IPublishEndpoint>();
        var sagaLocking = scope.ServiceProvider
            .GetRequiredService<ISagaLocking>();

        // Find sagas that need processing — not yet completed or cancelled
        var pendingSagas = await db.SagaStates
            .Where(s =>
                s.Status != SagaStatus.Completed &&
                s.Status != SagaStatus.Cancelled &&
                s.Status != SagaStatus.Failed)
            .OrderBy(s => s.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        foreach (var saga in pendingSagas)
        {
            try
            {
                await ProcessSaga(saga, db, publishEndpoint, sagaLocking, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Saga {SagaId} for Order {OrderId} failed unexpectedly",
                    saga.Id, saga.OrderId);

                saga.FailureReason = ex.Message;
                saga.RetryCount++;
                saga.UpdatedAt = DateTime.UtcNow;

                if (saga.RetryCount >= 3)
                {
                    saga.Status = SagaStatus.Failed;
                    await CancelOrderAsync(saga, db, ct);
                    _logger.LogError(
                        "Saga {SagaId} marked as Failed after {Retries} retries",
                        saga.Id, saga.RetryCount);
                }

                await db.SaveChangesAsync(ct);

                if (saga.Status == SagaStatus.Failed)
                    OrderMetrics.RecordOrderFailed(FailReason.OrchestratorError);
            }
        }
    }

    // The timeout service skips Failed sagas, so without this the order would stay Pending for ever.
    // The orchestrator only fails on the step that sends the first command, before anything is
    // charged. The customer is told a fixed message; the exception text stays on the saga.
    private static async Task CancelOrderAsync(OrderSagaState saga, OrderDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.FindAsync([saga.OrderId], ct);
        if (order is not { Status: OrderStatus.Pending }) return;

        var now = DateTime.UtcNow;
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;

        var summary = await db.OrderSummaries.FirstOrDefaultAsync(s => s.OrderId == saga.OrderId, ct);
        if (summary is null) return;
        summary.StatusName = OrderStatus.Cancelled.ToString();
        summary.StatusCode = (int)OrderStatus.Cancelled;
        summary.FailureReason = FailureReasons.OrderCouldNotBeProcessed;
        summary.UpdatedAt = now;
    }

    // internal: a test runs one saga through it without the 3-second polling loop
    internal async Task ProcessSaga(
        OrderSagaState saga,
        OrderDbContext db,
        IPublishEndpoint publishEndpoint,
        ISagaLocking sagaLocking,
        CancellationToken ct)
    {
        // No HTTP request here: use the id the saga was started with, so every log line below
        // and the commands published belong to the same trace as the request that placed the order
        using var correlation = CorrelationContext.Push(saga.CorrelationId);

        _logger.LogInformation(
            "Processing saga {SagaId} for Order {OrderId} — Status: {Status}",
            saga.Id, saga.OrderId, saga.Status);

        switch (saga.Status)
        {
            // ── Step 1: Just started → send ReserveInventoryCommand ───────────
            case SagaStatus.Started:
                await HandleStarted(saga, db, publishEndpoint, sagaLocking, ct);
                break;

            // ── Step 2: Inventory reserved → send ProcessPaymentCommand ───────
            case SagaStatus.ReservingInventory:
                // Still waiting for InventoryReservedEvent
                // The consumer will update the status when it arrives
                _logger.LogDebug(
                    "Saga {SagaId} waiting for inventory reservation", saga.Id);
                break;

            // ── Step 3: Payment processing → wait for result ──────────────────
            case SagaStatus.ProcessingPayment:
                _logger.LogDebug(
                    "Saga {SagaId} waiting for payment result", saga.Id);
                break;

            // ── Step 4: Completing → wait for the inventory commit result ──────
            // InventoryCommittedConsumer completes the saga (or InventoryCommitFailedConsumer
            // refunds and fails it). SagaTimeoutService re-sends the commit if nothing arrives.
            case SagaStatus.Completing:
                _logger.LogDebug(
                    "Saga {SagaId} waiting for inventory commit result", saga.Id);
                break;

            // ── Step 5: Releasing inventory → compensating transaction ─────────
            case SagaStatus.ReleasingInventory:
                _logger.LogDebug(
                    "Saga {SagaId} waiting for inventory release", saga.Id);
                break;
        }
    }

    private async Task HandleStarted(
        OrderSagaState saga,
        OrderDbContext db,
        IPublishEndpoint publishEndpoint,
        ISagaLocking sagaLocking,
        CancellationToken ct)
    {
        ReserveInventoryCommand command;

        // Claim the saga: lock its row and re-check the state. Without this, two Order
        // instances polling the same table would both start it, and a cancel arriving at the
        // same moment would be overwritten by the orchestrator's stale copy.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var locked = await sagaLocking.LockByOrderIdAsync(db, saga.OrderId, ct);
            if (locked is null || locked.Status != SagaStatus.Started)
            {
                _logger.LogInformation(
                    "Saga {SagaId}: no longer in Started state — skipping", saga.Id);
                return;
            }

            _logger.LogInformation(
                "Saga {SagaId}: Sending ReserveInventoryCommand for Order {OrderId}",
                locked.Id, locked.OrderId);

            command = new ReserveInventoryCommand
            {
                OrderId = locked.OrderId,
                Items = JsonSerializer.Deserialize<List<OrderLineItem>>(locked.ItemsJson)!
            };

            // Save the new state BEFORE publishing: the reply can arrive within milliseconds
            // and InventoryReservedConsumer only accepts it while the saga is ReservingInventory.
            locked.Status = SagaStatus.ReservingInventory;
            locked.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        try
        {
            await publishEndpoint.Publish(command, ct);
        }
        catch
        {
            // Command never left — go back to Started so the next poll retries it
            // (unless something else, e.g. a cancel, has moved the saga on in the meantime)
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var locked = await sagaLocking.LockByOrderIdAsync(db, saga.OrderId, CancellationToken.None);
            if (locked is not null && locked.Status == SagaStatus.ReservingInventory)
            {
                locked.Status = SagaStatus.Started;
                locked.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }
            throw;
        }
    }
}
