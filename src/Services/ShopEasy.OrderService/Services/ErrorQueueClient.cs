using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ShopEasy.OrderService.Services;

// The error queues MassTransit creates (<endpoint>_error) for messages that failed all retries.
// This is the single list the dead-letter monitor watches and the requeue endpoint accepts —
// the endpoint refuses any other queue name.
public static class ErrorQueues
{
    public const string Suffix = "_error";

    // Add new ones as you add consumers
    public static readonly IReadOnlyList<string> Monitored =
    [
        "inventory-reserve_error",
        "inventory-commit_error",
        "inventory-release_error",
        "payment-process_error",
        "order-saga-starter_error",
        "payment-refund_error",
        "order-saga-inventory-reserved_error",
        "order-saga-inventory-failed_error",
        "order-saga-inventory-committed_error",
        "order-saga-inventory-commit-failed_error",
        "order-saga-payment-succeeded_error",
        "order-saga-payment-failed_error",
        "order-saga-inventory-released_error"
    ];

    public static bool IsMonitored(string queueName) =>
        Monitored.Contains(queueName, StringComparer.Ordinal);

    // "payment-process_error" → "payment-process"
    public static string OriginalQueueOf(string errorQueue) =>
        errorQueue.EndsWith(Suffix, StringComparison.Ordinal)
            ? errorQueue[..^Suffix.Length]
            : errorQueue;
}

public record ErrorQueueStatus(string Queue, int MessageCount, int ConsumerCount);

public enum MoveStatus { Moved, QueueNotFound }

public record MoveResult(MoveStatus Status, int Moved);

// Raised when the broker cannot be reached or refuses the operation.
public class BrokerUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

// Inspects and drains error queues over AMQP — the same protocol and credentials the service
// already uses for messaging — so the RabbitMQ management plugin (HTTP, port 15672) is not
// needed by the application. It stays available as a tool for humans only.
public interface IErrorQueueClient
{
    // Message and consumer count for each queue that exists; queues that do not exist yet are
    // left out (MassTransit creates an error queue only when the first message fails).
    Task<IReadOnlyList<ErrorQueueStatus>> GetStatusAsync(
        IEnumerable<string> queueNames, CancellationToken ct = default);

    // Moves every message currently in <paramref name="fromQueue"/> to <paramref name="toQueue"/>.
    // Each message is published (and confirmed by the broker) before it is removed from the
    // source, so a crash can duplicate a message but never lose one. Consumers are idempotent.
    Task<MoveResult> MoveAllAsync(
        string fromQueue, string toQueue, CancellationToken ct = default);
}

public class RabbitMqErrorQueueClient(
    IConfiguration configuration,
    ILogger<RabbitMqErrorQueueClient> logger) : IErrorQueueClient
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    public Task<IReadOnlyList<ErrorQueueStatus>> GetStatusAsync(
        IEnumerable<string> queueNames, CancellationToken ct = default)
    {
        var names = queueNames.ToList();
        // The RabbitMQ client API is synchronous — keep it off the request/worker thread
        return Task.Run<IReadOnlyList<ErrorQueueStatus>>(() =>
        {
            using var connection = Connect();
            var result = new List<ErrorQueueStatus>();

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();

                // A passive declare of a missing queue closes its channel, so use one per queue
                using var channel = connection.CreateModel();
                try
                {
                    var queue = channel.QueueDeclarePassive(name);
                    result.Add(new ErrorQueueStatus(
                        name, (int)queue.MessageCount, (int)queue.ConsumerCount));
                }
                catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
                {
                    // queue does not exist (yet) — nothing has failed there
                }
            }

            return result;
        }, ct);
    }

    public Task<MoveResult> MoveAllAsync(
        string fromQueue, string toQueue, CancellationToken ct = default)
        => Task.Run(() => MoveAll(fromQueue, toQueue, ct), ct);

    private MoveResult MoveAll(string fromQueue, string toQueue, CancellationToken ct)
    {
        using var connection = Connect();
        using var channel = connection.CreateModel();

        uint waiting;
        try
        {
            waiting = channel.QueueDeclarePassive(fromQueue).MessageCount;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return new MoveResult(MoveStatus.QueueNotFound, 0);
        }

        // Publisher confirms: the broker acknowledges that the copy is safely in the destination
        channel.ConfirmSelect();

        var unroutable = false;
        channel.BasicReturn += (_, _) => unroutable = true;

        var moved = 0;

        // Bounded by the count at the start: a message that fails again goes back into the error
        // queue asynchronously and must not make this loop run forever.
        for (var i = 0u; i < waiting; i++)
        {
            ct.ThrowIfCancellationRequested();

            var message = channel.BasicGet(fromQueue, autoAck: false);
            if (message is null) break; // drained by someone else

            try
            {
                unroutable = false;
                // Default exchange + routing key = destination queue name. mandatory:true makes
                // the broker return the message if that queue does not exist.
                channel.BasicPublish(
                    exchange: "",
                    routingKey: toQueue,
                    mandatory: true,
                    basicProperties: message.BasicProperties,
                    body: message.Body);
                channel.WaitForConfirmsOrDie(ConfirmTimeout);

                if (unroutable)
                    throw new InvalidOperationException(
                        $"Destination queue '{toQueue}' does not exist.");

                channel.BasicAck(message.DeliveryTag, multiple: false);
                moved++;
            }
            catch
            {
                // Put the message back where it was; everything moved so far stays moved
                try { channel.BasicNack(message.DeliveryTag, multiple: false, requeue: true); }
                catch (Exception nackError)
                {
                    logger.LogWarning(nackError,
                        "Could not return a message to {Queue}; the broker will requeue it " +
                        "when the channel closes", fromQueue);
                }
                throw;
            }
        }

        logger.LogInformation(
            "Moved {Count} message(s) from {From} to {To}", moved, fromQueue, toQueue);
        return new MoveResult(MoveStatus.Moved, moved);
    }

    private IConnection Connect()
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "rabbitmq",
            Port = configuration.GetValue("RabbitMQ:Port", AmqpTcpEndpoint.UseDefaultPort),
            VirtualHost = "/",
            UserName = configuration["RabbitMQ:Username"]
                ?? throw new InvalidOperationException("RabbitMQ:Username is not configured."),
            Password = configuration["RabbitMQ:Password"]
                ?? throw new InvalidOperationException("RabbitMQ:Password is not configured."),
            ClientProvidedName = "shopeasy-order-error-queues",
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            AutomaticRecoveryEnabled = false
        };

        try
        {
            return factory.CreateConnection();
        }
        catch (Exception ex) when (ex is BrokerUnreachableException or AlreadyClosedException
                                       or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new BrokerUnavailableException("RabbitMQ is not reachable.", ex);
        }
    }
}
