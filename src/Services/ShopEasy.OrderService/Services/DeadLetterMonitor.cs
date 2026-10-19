using Microsoft.Extensions.Options;

namespace ShopEasy.OrderService.Services;

// Background service that monitors the error queues of all services.
// Logs an alert when messages land in an error queue and exposes the queue state through the
// admin API (/api/outbox/deadletters) for inspection and requeue.
//
// Talks AMQP through IErrorQueueClient — it does not need the RabbitMQ management plugin.
public class DeadLetterMonitor : BackgroundService
{
    private readonly IErrorQueueClient _errorQueues;
    private readonly IConfiguration _config;
    private readonly ILogger<DeadLetterMonitor> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private readonly GuardedPass _guard;
    private bool _brokerWasUnavailable;

    public DeadLetterMonitor(
        IErrorQueueClient errorQueues,
        IConfiguration config,
        ILogger<DeadLetterMonitor> logger,
        IOptions<BackgroundLoopOptions>? loops = null)
    {
        _errorQueues = errorQueues;
        _config = config;
        _logger = logger;
        _guard = new GuardedPass(logger, "Dead letter monitor", loops);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dead letter monitor started. Checking every {Interval}s",
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _guard.RunAsync(CheckErrorQueues, stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    // One check cycle. Internal so tests can run it without the 30-second timer.
    internal async Task CheckErrorQueues(CancellationToken ct)
    {
        IReadOnlyList<ErrorQueueStatus> queues;
        try
        {
            queues = await _errorQueues.GetStatusAsync(ErrorQueues.Monitored, ct);
            _brokerWasUnavailable = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broker down or restarting: say so once, not every 30 seconds
            if (!_brokerWasUnavailable)
                _logger.LogWarning(ex,
                    "Dead letter check skipped: RabbitMQ is not reachable");
            _brokerWasUnavailable = true;
            return;
        }

        foreach (var queue in queues.Where(q => q.MessageCount > 0))
        {
            _logger.LogError(
                "DEAD LETTER ALERT: Queue {Queue} has {Count} failed message(s). " +
                "Inspect them in the RabbitMQ management UI and requeue with " +
                "POST /api/outbox/deadletters/{Queue}/requeue once the cause is fixed.",
                queue.Queue, queue.MessageCount, queue.Queue);
        }
    }

    // Used by the admin API endpoint
    public async Task<List<DeadLetterQueueInfo>> GetAllErrorQueues(CancellationToken ct = default)
    {
        var queues = await _errorQueues.GetStatusAsync(ErrorQueues.Monitored, ct);

        // Optional link for humans, e.g. http://localhost:15672 — never used by the service itself
        var managementUi = _config["RabbitMQ:ManagementUiUrl"]?.TrimEnd('/');

        return queues.Select(q => new DeadLetterQueueInfo
        {
            QueueName = q.Queue,
            MessageCount = q.MessageCount,
            ConsumerCount = q.ConsumerCount,
            ManagementUrl = managementUi is null
                ? null
                : $"{managementUi}/#/queues/%2F/{Uri.EscapeDataString(q.Queue)}"
        }).ToList();
    }
}

public class DeadLetterQueueInfo
{
    public string QueueName { get; set; } = default!;
    public int MessageCount { get; set; }
    public int ConsumerCount { get; set; }
    public string? ManagementUrl { get; set; }
    public bool HasMessages => MessageCount > 0;
}
