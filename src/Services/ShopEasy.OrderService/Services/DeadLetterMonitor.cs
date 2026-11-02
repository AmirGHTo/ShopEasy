using Microsoft.Extensions.Options;
using ShopEasy.OrderService.Metrics;

namespace ShopEasy.OrderService.Services;

// Background service that monitors the error queues of all services.
// Every pass it exports how many messages wait in each error queue (shopeasy_error_queue_messages, the
// DeadLettersPresent alert), logs an alert for the queues that hold messages, and the queue state is
// available through the admin API (/api/outbox/deadletters) for inspection and requeue.
//
// A pass that cannot read the broker FAILS (it is counted, and PollingLoopStalled fires if it goes on):
// it must never export 0, because "I could not look" is not "there is nothing".
//
// Talks AMQP through IErrorQueueClient — it does not need the RabbitMQ management plugin.
public class DeadLetterMonitor : BackgroundService
{
    private readonly IErrorQueueClient _errorQueues;
    private readonly IConfiguration _config;
    private readonly ILogger<DeadLetterMonitor> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private readonly GuardedPass _guard;

    // The queues to look at; tests point it at their own
    internal IReadOnlyList<string> Queues { get; init; } = ErrorQueues.Monitored;

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
    // An unreachable broker is not handled here: the exception ends the pass, and the guard counts
    // it as failed and logs the outage once.
    internal async Task CheckErrorQueues(CancellationToken ct)
    {
        var queues = await _errorQueues.GetStatusAsync(Queues, ct);

        // A queue is created when its first message fails, so one that is missing has nothing in it
        var counts = queues.ToDictionary(q => q.Queue, q => q.MessageCount);
        foreach (var queue in Queues)
            OrderMetrics.SetErrorQueueMessages(queue, counts.GetValueOrDefault(queue));

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
        var queues = await _errorQueues.GetStatusAsync(Queues, ct);

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
