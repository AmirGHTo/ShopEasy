using Humanizer;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Core;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;

namespace ShopEasy.OrderService.Services;

// Background service that monitors sagas for timeouts.
// If a saga is stuck in a non-terminal state for too long,
// it applies the appropriate compensating action automatically.
public class SagaTimeoutService(
    IServiceScopeFactory scopeFactory,
    ILogger<SagaTimeoutService> logger,
    IOptions<BackgroundLoopOptions>? loops = null) : BackgroundService
{
    private readonly GuardedPass _guard = new(logger, "Saga timeout service", loops);
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<SagaTimeoutService> _logger = logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    // How long a saga may go without progress before the timeout service steps in
    public static readonly TimeSpan SagaTimeout = TimeSpan.FromMinutes(2);
    private const int MaxReleaseAttempts = 3;
    private const int PaidCancellationAttemptsBeforeAlarm = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Saga timeout service started. " +
            "Checking every {Interval}s, timeout after {Timeout}s",
            _checkInterval.TotalSeconds,
            SagaTimeout.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await _guard.RunAsync(CheckForTimedOutSagas, ct);
            await Task.Delay(_checkInterval, ct);
        }
    }

    // One pass. The running service (through CheckForTimedOutSagas) and the tests both call this, so a
    // test exercises the code that runs.
    //
    // Not settled means the service has just started or its last pass failed. A saga can then look stuck
    // only because nothing was running (the database or the whole Order service was away), and the
    // orchestrator, which polls every 3 s, should get to it before the timeout cancels the order.
    // So nothing is timed out in that pass; the next one does.
    public async Task CheckForTimedOutSagasPublic(
        OrderDbContext db,
        IPublishEndpoint publishEndpoint,
        ISagaLocking sagaLocking,
        CancellationToken ct,
        bool settled = true)
    {
        var timeoutThreshold = DateTime.UtcNow - SagaTimeout;

        await PublishInFlightGauges(db, ct);

        var timedOutSagas = await InFlight(db)
            .Where(s => s.UpdatedAt < timeoutThreshold)
            .ToListAsync(ct);

        if (!timedOutSagas.Any()) return;

        if (!settled)
        {
            _logger.LogInformation(
                "{Count} saga(s) look timed out, but this is the first pass since the service started or failed: " +
                "waiting one interval so the orchestrator and the consumers can catch up first", timedOutSagas.Count);
            return;
        }

        _logger.LogWarning(
            "Found {Count} timed out saga(s)", timedOutSagas.Count);

        foreach (var saga in timedOutSagas)
            await HandleTimedOutSaga(saga, db, publishEndpoint, sagaLocking, ct);
    }

    // Sagas that have neither finished nor given up
    private static IQueryable<OrderSagaState> InFlight(OrderDbContext db) => db.SagaStates.Where(s =>
        s.Status != SagaStatus.Completed &&
        s.Status != SagaStatus.Cancelled &&
        s.Status != SagaStatus.Failed);

    // Read from the database on every cycle, so each instance reports the same value and a restart
    // loses nothing; the age of the oldest one is what rises when an order is stuck
    private static async Task PublishInFlightGauges(OrderDbContext db, CancellationToken ct)
    {
        var count = await InFlight(db).CountAsync(ct);
        var oldest = count == 0 ? null : (DateTime?)await InFlight(db).MinAsync(s => s.CreatedAt, ct);
        OrderMetrics.SetSagasInFlight(count, oldest is null ? 0 : (DateTime.UtcNow - oldest.Value).TotalSeconds);
    }

    private async Task CheckForTimedOutSagas(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        await CheckForTimedOutSagasPublic(
            scope.ServiceProvider.GetRequiredService<OrderDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(),
            scope.ServiceProvider.GetRequiredService<ISagaLocking>(),
            ct,
            _guard.Settled);
    }

    internal async Task HandleTimedOutSaga(
        OrderSagaState saga,
        OrderDbContext db,
        IPublishEndpoint publishEndpoint,
        ISagaLocking sagaLocking,
        CancellationToken ct)
    {
        // Same trace as the request that placed the order (there is no request here)
        using var correlation = CorrelationContext.Push(saga.CorrelationId);

        // Re-read WITH a row lock: an event consumer (or a cancel) may be changing this
        // saga right now, and we must act on its outcome, not on our stale scan result.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var lockedSaga = await sagaLocking.LockByOrderIdAsync(db, saga.OrderId, ct);

            if (lockedSaga is null)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            // The saga made progress between our scan and acquiring the lock — not timed out
            if (lockedSaga.UpdatedAt >= DateTime.UtcNow - SagaTimeout)
            {
                logger.LogDebug(
                    "Saga {SagaId} progressed while waiting for the lock — no longer timed out",
                    saga.Id);
                await transaction.RollbackAsync(ct);
                return;
            }

            // Re-check status after acquiring lock
            // Another thread may have updated it between our scan and this lock
            if (lockedSaga.Status == SagaStatus.Completed ||
                lockedSaga.Status == SagaStatus.Cancelled ||
                lockedSaga.Status == SagaStatus.Failed)
            {
                logger.LogDebug(
                    "Saga {SagaId} was already resolved by the time we acquired the lock",
                    saga.Id);
                await transaction.RollbackAsync(ct);
                return;
            }

            // Now safe to apply timeout compensation
            await ApplyTimeoutCompensation(lockedSaga, db, publishEndpoint, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            logger.LogError(ex,
                "Failed to handle timed out saga {SagaId}", saga.Id);
        }
    }

    private async Task ApplyTimeoutCompensation(
        OrderSagaState saga,
        OrderDbContext db,
        IPublishEndpoint publishEndpoint,
        CancellationToken ct)
    {
        logger.LogError(
            "Saga {SagaId} timed out in {Status} after {Duration}s — compensating",
            saga.Id, saga.Status,
            (DateTime.UtcNow - saga.CreatedAt).TotalSeconds);

        switch (saga.Status)
        {
            case SagaStatus.Started:
            case SagaStatus.ReservingInventory:
                await CancelOrder(saga, db,
                    $"Timed out waiting for inventory ({saga.Status})", CancelReason.Timeout);
                break;

            case SagaStatus.ProcessingPayment:
                await publishEndpoint.Publish(
                    new ReleaseInventoryCommand { OrderId = saga.OrderId }, ct);
                saga.Status = SagaStatus.ReleasingInventory;
                saga.FailureReason = FailureReasons.TimedOutWaitingForPayment;
                saga.UpdatedAt = DateTime.UtcNow;
                break;

            case SagaStatus.ReleasingInventory:
                // The release command (or its confirmation) got lost. Re-send it — release is
                // idempotent — and only give up, flagging it for a human, after a few attempts.
                if (saga.RetryCount < MaxReleaseAttempts)
                {
                    await publishEndpoint.Publish(
                        new ReleaseInventoryCommand { OrderId = saga.OrderId }, ct);
                    saga.RetryCount++;
                    saga.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    logger.LogCritical(
                        "Saga {SagaId}: inventory release for Order {OrderId} was not confirmed after " +
                        "{Attempts} attempts. Reserved stock may be leaked — MANUAL INTERVENTION REQUIRED.",
                        saga.Id, saga.OrderId, saga.RetryCount);
                    await CancelOrder(saga, db,
                        "Timed out during inventory release compensation", CancelReason.ReleaseNotConfirmed);
                }
                break;

            case SagaStatus.RefundingPayment:
            case SagaStatus.RestockingInventory:
                // A paid order is being cancelled and a command or its answer got lost. Refund and
                // restock are idempotent and answer again, so send the command again. Money is
                // involved, so never give up on it or cancel the order on a guess: after a few attempts
                // keep trying, at the same pace, and log it as critical for a person.
                if (saga.RetryCount >= PaidCancellationAttemptsBeforeAlarm)
                    logger.LogCritical(
                        "Saga {SagaId}: {Status} for Order {OrderId} was not answered after {Attempts} attempts — " +
                        "MANUAL INTERVENTION REQUIRED (the customer's money or the stock is in between).",
                        saga.Id, saga.Status, saga.OrderId, saga.RetryCount);

                if (saga.Status == SagaStatus.RefundingPayment)
                    await publishEndpoint.Publish(new RefundPaymentCommand
                    {
                        OrderId = saga.OrderId,
                        Reason = saga.FailureReason ?? "Cancelled"
                    }, ct);
                else
                    await publishEndpoint.Publish(new RestockInventoryCommand { OrderId = saga.OrderId }, ct);

                saga.RetryCount++;
                saga.UpdatedAt = DateTime.UtcNow;
                break;

            case SagaStatus.Completing:
                if (saga.RetryCount >= 10)
                {
                    logger.LogCritical(
                        "Saga {SagaId}: CommitInventory stuck after {Retry} retries. " +
                        "MANUAL INTERVENTION REQUIRED for Order {OrderId}",
                        saga.Id, saga.RetryCount, saga.OrderId);
                    if (!FailureReasons.IsCancelRequest(saga.FailureReason)) // keep a cancellation asked for
                        saga.FailureReason = $"CommitInventory stuck after {saga.RetryCount} retries";
                    saga.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    await publishEndpoint.Publish(
                        new CommitInventoryCommand { OrderId = saga.OrderId }, ct);
                    saga.RetryCount++;
                    saga.UpdatedAt = DateTime.UtcNow;
                }
                break;
        }
    }

    private async Task CancelOrder(
        OrderSagaState saga,
        OrderDbContext db,
        string reason,
        string metricReason)
    {
        // Update order status to Cancelled
        var order = await db.Orders.FindAsync(saga.OrderId);
        if (order is not null)
        {
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }

        // Keep the read model (what the customer sees) in sync
        var summary = await db.OrderSummaries.FirstOrDefaultAsync(s => s.OrderId == saga.OrderId);
        if (summary is not null)
        {
            summary.StatusName = OrderStatus.Cancelled.ToString();
            summary.StatusCode = (int)OrderStatus.Cancelled;
            summary.FailureReason = reason;
            summary.UpdatedAt = DateTime.UtcNow;
        }

        // Mark saga as failed
        saga.Status = SagaStatus.Failed;
        saga.FailureReason = reason;
        saga.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        OrderMetrics.RecordOrderCancelled(metricReason);
        _logger.LogWarning(
            "Saga {SagaId}: Order {OrderId} cancelled due to timeout. Reason: {Reason}",
            saga.Id, saga.OrderId, reason);
    }

    public async Task CheckForTimedOutSagasPublic(CancellationToken ct)
        => await CheckForTimedOutSagas(ct);
}