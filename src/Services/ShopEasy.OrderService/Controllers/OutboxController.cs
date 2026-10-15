using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Security;

namespace ShopEasy.OrderService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = PolicyNames.OrdersMonitor)]
public class OutboxController(
    OrderDbContext db,
    DeadLetterMonitor deadLetterMonitor,
    IErrorQueueClient errorQueues,
    ILogger<OutboxController> logger) : ControllerBase
{
    private readonly OrderDbContext _db = db;
    private readonly DeadLetterMonitor _deadLetterMonitor = deadLetterMonitor;
    private readonly IErrorQueueClient _errorQueues = errorQueues;
    private readonly ILogger<OutboxController> _logger = logger;

    #region outbox monitor

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        const int maxRetries = 5;

        var allEntries = await _db.OutboxEntries
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .ToListAsync();

        var pending = allEntries.Where(e => e.DeliveredAt == null
                                           && e.RetryCount < maxRetries).ToList();
        var delivered = allEntries.Where(e => e.DeliveredAt != null).ToList();
        var deadLetter = allEntries.Where(e => e.DeliveredAt == null
                                           && e.RetryCount >= maxRetries).ToList();

        return Ok(new
        {
            total = allEntries.Count,
            pending = pending.Count,
            delivered = delivered.Count,
            deadLetter = deadLetter.Count,
            message = pending.Count == 0 && deadLetter.Count == 0
                ? "Outbox is empty — all messages delivered"
                : $"{pending.Count} pending, {deadLetter.Count} dead letter",
            entries = allEntries.Select(e => new
            {
                e.Id,
                e.EventType,
                e.CreatedAt,
                e.DeliveredAt,
                e.RetryCount,
                IsDeadLetter = e.DeliveredAt == null && e.RetryCount >= maxRetries,
                Payload = e.Payload.Length > 300
                    ? e.Payload[..300] + "..."
                    : e.Payload
            })
        });
    }



    [HttpGet("delivered")]
    public async Task<IActionResult> GetDelivered()
    {
        var delivered = await _db.OutboxEntries
            .Where(e => e.DeliveredAt != null)
            .OrderByDescending(e => e.DeliveredAt)
            .Take(20)
            .Select(e => new
            {
                e.Id,
                e.EventType,
                e.CreatedAt,
                e.DeliveredAt,
                e.RetryCount,
                DeliveryDelayMs = (int)(e.DeliveredAt!.Value - e.CreatedAt).TotalMilliseconds
            })
            .ToListAsync();

        return Ok(new { count = delivered.Count, items = delivered });
    }

    #endregion

    #region saga monitor

    [HttpGet("sagas")]
    public async Task<IActionResult> GetSagas()
    {
        var sagas = await _db.SagaStates
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync();

        var now = DateTime.UtcNow;

        return Ok(sagas.Select(s => new
        {
            s.Id,
            s.OrderId,
            s.Status,
            s.FailureReason,
            s.RetryCount,
            s.CreatedAt,
            s.UpdatedAt,
            DurationMs = (int)(s.UpdatedAt - s.CreatedAt).TotalMilliseconds,
            IsTimedOut = s.Status != SagaStatus.Completed
                          && s.Status != SagaStatus.Cancelled
                          && s.Status != SagaStatus.Failed
                          && s.UpdatedAt < now - TimeSpan.FromMinutes(2),
            AgeSeconds = (int)(now - s.CreatedAt).TotalSeconds
        }));
    }

    #endregion

    #region deadletter monitor

    // ── GET /api/outbox/deadletters ───────────────────────────────────────────
    // Shows all error queues and their message counts
    [HttpGet("deadletters")]
    public async Task<IActionResult> GetDeadLetters(CancellationToken ct)
    {
        try
        {
            var queues = await _deadLetterMonitor.GetAllErrorQueues(ct);
            return Ok(new
            {
                totalFailedMessages = queues.Sum(q => q.MessageCount),
                hasFailures = queues.Any(q => q.HasMessages),
                queues = queues
            });
        }
        catch (BrokerUnavailableException ex)
        {
            _logger.LogWarning(ex, "Dead letter listing failed: RabbitMQ is not reachable");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "RabbitMQ is not reachable. Try again shortly." });
        }
    }

    // ── POST /api/outbox/deadletters/{queueName}/requeue ──────────────────────
    // Moves the messages of an error queue back to the queue they came from so the consumer
    // processes them again. Only the known error queues (see ErrorQueues) are accepted.
    // Safe to repeat: every consumer is idempotent, so a message that is processed twice
    // (for example after a crash in the middle of the move) changes nothing the second time.
    //   200 moved (count in the body) · 400 not a known error queue
    //   404 the error queue does not exist (nothing has failed there) · 503 RabbitMQ unreachable
    [HttpPost("deadletters/{queueName}/requeue")]
    [Authorize(Policy = PolicyNames.OrdersMonitorWrite)]
    public async Task<IActionResult> Requeue(string queueName, CancellationToken ct)
    {
        if (!ErrorQueues.IsMonitored(queueName))
            return BadRequest(new { message = "Can only requeue from a known _error queue" });

        var originalQueue = ErrorQueues.OriginalQueueOf(queueName);

        try
        {
            var result = await _errorQueues.MoveAllAsync(queueName, originalQueue, ct);

            if (result.Status == MoveStatus.QueueNotFound)
                return NotFound(new { message = $"{queueName} does not exist — nothing has failed there." });

            _logger.LogInformation(
                "Messages requeued from {ErrorQueue} to {OriginalQueue}: {Count}",
                queueName, originalQueue, result.Moved);

            return Ok(new
            {
                message = $"Moved {result.Moved} message(s) from {queueName} to {originalQueue}",
                originalQueue,
                moved = result.Moved
            });
        }
        catch (BrokerUnavailableException ex)
        {
            _logger.LogWarning(ex, "Requeue from {QueueName} failed: RabbitMQ is not reachable", queueName);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "RabbitMQ is not reachable. Try again shortly." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to requeue from {QueueName}", queueName);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Requeue failed. Messages already moved stay moved; the rest remain in the error queue." });
        }
    }
    #endregion
}