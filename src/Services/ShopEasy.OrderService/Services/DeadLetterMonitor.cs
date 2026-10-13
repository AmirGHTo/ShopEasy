using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopEasy.OrderService.Services;

// Background service that monitors error queues across all services.
// Logs alerts when messages land in error queues.
// Exposes dead letter data via API for inspection and requeue.
public class DeadLetterMonitor : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DeadLetterMonitor> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    // Queues to monitor — add new ones as you add consumers
    private static readonly string[] MonitoredErrorQueues =
    [
        "inventory-reserve_error",
        "inventory-commit_error",
        "inventory-release_error",
        "payment-process_error",
        "order-saga-starter_error",
        "order-saga-inventory-reserved_error",
        "order-saga-payment-succeeded_error",
        "order-saga-payment-failed_error",
        "order-saga-inventory-released_error"
    ];

    public DeadLetterMonitor(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<DeadLetterMonitor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dead letter monitor started. Checking every {Interval}s",
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckErrorQueues(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CheckErrorQueues(CancellationToken ct)
    {
        var client = CreateManagementClient();

        foreach (var queue in MonitoredErrorQueues)
        {
            try
            {
                var count = await GetQueueMessageCount(client, queue, ct);
                if (count > 0)
                {
                    _logger.LogError(
                        "DEAD LETTER ALERT: Queue {Queue} has {Count} failed message(s). " +
                        "Check http://localhost:15672/#/queues/%2F/{Queue} for details.",
                        queue, count, queue);
                }
            }
            catch
            {
                // Queue doesn't exist yet — that's fine, ignore
            }
        }
    }

    private async Task<int> GetQueueMessageCount(
        HttpClient client, string queue, CancellationToken ct)
    {
        var host = _config["RabbitMQ:Host"] ?? "rabbitmq";
        var res = await client.GetAsync(
            $"http://{host}:15672/api/queues/%2F/{Uri.EscapeDataString(queue)}", ct);

        if (!res.IsSuccessStatusCode) return 0;

        var json = await res.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("messages").GetInt32();
    }

    private HttpClient CreateManagementClient()
    {
        var client = _httpClientFactory.CreateClient();
        var user = _config["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username is not configured.");
        var pass = _config["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password is not configured.");
        var auth = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes($"{user}:{pass}"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", auth);
        return client;
    }

    // Public method for the API endpoint to call
    public async Task<List<DeadLetterQueueInfo>> GetAllErrorQueues()
    {
        var client = CreateManagementClient();
        var result = new List<DeadLetterQueueInfo>();

        foreach (var queue in MonitoredErrorQueues)
        {
            try
            {
                var host = _config["RabbitMQ:Host"] ?? "rabbitmq";
                var res = await client.GetAsync(
                    $"http://{host}:15672/api/queues/%2F/{Uri.EscapeDataString(queue)}");

                if (!res.IsSuccessStatusCode) continue;

                var json = await res.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                result.Add(new DeadLetterQueueInfo
                {
                    QueueName = queue,
                    MessageCount = root.GetProperty("messages").GetInt32(),
                    ConsumerCount = root.TryGetProperty("consumers", out var c)
                        ? c.GetInt32() : 0,
                    ManagementUrl = $"http://localhost:15672/#/queues/%2F/{queue}"
                });
            }
            catch
            {
                // Queue doesn't exist — skip
            }
        }

        return result;
    }
}

public class DeadLetterQueueInfo
{
    public string QueueName { get; set; } = default!;
    public int MessageCount { get; set; }
    public int ConsumerCount { get; set; }
    public string ManagementUrl { get; set; } = default!;
    public bool HasMessages => MessageCount > 0;
}