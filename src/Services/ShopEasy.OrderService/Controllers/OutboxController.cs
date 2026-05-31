using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog.Core;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using System.Text.Json;

namespace ShopEasy.OrderService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class OutboxController(
    OrderDbContext db,
    DeadLetterMonitor deadLetterMonitor,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<OrderCommandsController> logger) : ControllerBase
{
    private readonly OrderDbContext _db = db;
    private readonly DeadLetterMonitor _deadLetterMonitor = deadLetterMonitor;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<OrderCommandsController> _logger = logger;

    #region outbox monitor

    [HttpGet("pending")]
    [AllowAnonymous]
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

    [HttpGet("debug")]
    [AllowAnonymous]
    public async Task<IActionResult> Debug()
    {
        var tables = await _db.Database
            .SqlQueryRaw<string>(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
            .ToListAsync();

        var all = await _db.OutboxEntries
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .ToListAsync();

        return Ok(new
        {
            tables,
            total = all.Count,
            pending = all.Count(e => e.DeliveredAt == null),
            delivered = all.Count(e => e.DeliveredAt != null),
            entries = all.Select(e => new
            {
                e.Id,
                e.EventType,
                e.CreatedAt,
                e.DeliveredAt,
                e.RetryCount
            })
        });
    }

    #endregion

    #region saga monitor

    [HttpGet("sagas")]
    [AllowAnonymous]
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
    [AllowAnonymous]
    public async Task<IActionResult> GetDeadLetters()
    {
        var queues = await _deadLetterMonitor.GetAllErrorQueues();
        return Ok(new
        {
            totalFailedMessages = queues.Sum(q => q.MessageCount),
            hasFailures = queues.Any(q => q.HasMessages),
            queues = queues
        });
    }

    // ── POST /api/outbox/deadletters/{queueName}/requeue ──────────────────────
    // Moves messages from error queue back to the original queue for reprocessing
    [HttpPost("deadletters/{queueName}/requeue")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Requeue(string queueName)
    {
        // Safety check — only allow requeuing known error queues
        if (!queueName.EndsWith("_error"))
            return BadRequest(new { message = "Can only requeue from _error queues" });

        var originalQueue = queueName.Replace("_error", "");

        try
        {
            var host = _configuration["RabbitMQ:Host"] ?? "rabbitmq";
            var user = _configuration["RabbitMQ:Username"] ?? "guest";
            var pass = _configuration["RabbitMQ:Password"] ?? "guest";
            var auth = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{user}:{pass}"));

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            // RabbitMQ Management API — move messages between queues
            var moveBody = JsonSerializer.Serialize(new
            {
                vhost = "/",
                mode = "move",
                destination = originalQueue,
                destination_type = "queue"
            });

            var res = await client.PostAsync(
                $"http://{host}:15672/api/queues/%2F/{Uri.EscapeDataString(queueName)}/contents",
                new StringContent(moveBody,
                    System.Text.Encoding.UTF8, "application/json"));

            if (res.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Messages requeued from {ErrorQueue} to {OriginalQueue}",
                    queueName, originalQueue);
                return Ok(new
                {
                    message = $"Messages moved from {queueName} to {originalQueue}",
                    originalQueue = originalQueue
                });
            }

            return StatusCode(500, new { message = "Failed to requeue messages" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue from {QueueName}", queueName);
            return StatusCode(500, new { message = ex.Message });
        }
    }
    #endregion
}