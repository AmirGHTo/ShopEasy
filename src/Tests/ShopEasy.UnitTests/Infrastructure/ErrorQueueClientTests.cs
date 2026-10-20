using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// RabbitMqErrorQueueClient against a real RabbitMQ — AMQP only; the management plugin is not
// needed (and these tests do not use it). See InfrastructureFacts.cs.
public sealed class ErrorQueueClientTests : IDisposable
{
    private readonly List<string> _queues = [];
    private IConnection? _connection;
    private IModel? _channel;

    private IModel Channel
    {
        get
        {
            if (_channel is not null) return _channel;
            var factory = new ConnectionFactory { Uri = new Uri(TestInfrastructure.RabbitMq!) };
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();
            // Publisher confirms: Put returns only when the broker has the message in the queue
            _channel.ConfirmSelect();
            return _channel;
        }
    }

    private static RabbitMqErrorQueueClient CreateClient()
    {
        var uri = new Uri(TestInfrastructure.RabbitMq!);
        var credentials = uri.UserInfo.Split(':', 2);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = uri.Host,
            ["RabbitMQ:Port"] = (uri.Port > 0 ? uri.Port : 5672).ToString(),
            ["RabbitMQ:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMQ:Password"] = Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? "")
        }).Build();
        return new RabbitMqErrorQueueClient(configuration, NullLogger<RabbitMqErrorQueueClient>.Instance);
    }

    private string NewQueue(string name)
    {
        Channel.QueueDeclare(name, durable: false, exclusive: false, autoDelete: false);
        _queues.Add(name);
        return name;
    }

    private void Put(string queue, string body, string messageId)
    {
        var props = Channel.CreateBasicProperties();
        props.MessageId = messageId;
        props.ContentType = "application/vnd.masstransit+json";
        props.DeliveryMode = 2;
        props.Headers = new Dictionary<string, object> { ["MT-Reason"] = "fault" };
        Channel.BasicPublish("", queue, props, Encoding.UTF8.GetBytes(body));
        // The client under test reads the queue on its own connection. Without the confirm it can
        // run before the broker has taken the last message and move one message too few.
        Channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        foreach (var queue in _queues)
            try { Channel.QueueDelete(queue); } catch { /* already gone */ }
        _channel?.Dispose();
        _connection?.Dispose();
    }

    [RequiresRabbitMqFact]
    public async Task GetStatus_ReportsExistingQueuesAndSkipsMissingOnes()
    {
        var id = Guid.NewGuid().ToString("N");
        var error = NewQueue($"test-{id}_error");
        Put(error, "a", "m1");
        Put(error, "b", "m2");

        var status = await CreateClient().GetStatusAsync([error, $"test-{id}-missing_error"]);

        status.Should().ContainSingle().Which.Should().Be(new ErrorQueueStatus(error, 2, 0));
    }

    [RequiresRabbitMqFact]
    public async Task MoveAll_MovesEveryMessage_KeepingBodyAndProperties()
    {
        var id = Guid.NewGuid().ToString("N");
        var original = NewQueue($"test-{id}");
        var error = NewQueue($"test-{id}_error");
        Put(error, "first", "m1");
        Put(error, "second", "m2");
        Put(error, "third", "m3");

        var result = await CreateClient().MoveAllAsync(error, original);

        result.Should().Be(new MoveResult(MoveStatus.Moved, 3));
        Channel.MessageCount(error).Should().Be(0);
        Channel.MessageCount(original).Should().Be(3);

        var message = Channel.BasicGet(original, autoAck: true);
        Encoding.UTF8.GetString(message.Body.ToArray()).Should().Be("first");
        message.BasicProperties.MessageId.Should().Be("m1");
        message.BasicProperties.ContentType.Should().Be("application/vnd.masstransit+json");
        message.BasicProperties.DeliveryMode.Should().Be(2);
        Encoding.UTF8.GetString((byte[])message.BasicProperties.Headers["MT-Reason"]).Should().Be("fault");
    }

    [RequiresRabbitMqFact]
    public async Task MoveAll_OnAnEmptyQueue_MovesNothing()
    {
        var id = Guid.NewGuid().ToString("N");
        var original = NewQueue($"test-{id}");
        var error = NewQueue($"test-{id}_error");

        (await CreateClient().MoveAllAsync(error, original))
            .Should().Be(new MoveResult(MoveStatus.Moved, 0));
    }

    [RequiresRabbitMqFact]
    public async Task MoveAll_WhenTheSourceQueueDoesNotExist_ReportsNotFound()
    {
        var id = Guid.NewGuid().ToString("N");
        var original = NewQueue($"test-{id}");

        (await CreateClient().MoveAllAsync($"test-{id}-missing_error", original))
            .Should().Be(new MoveResult(MoveStatus.QueueNotFound, 0));
    }

    [RequiresRabbitMqFact]
    public async Task MoveAll_WhenTheDestinationDoesNotExist_FailsAndKeepsTheMessages()
    {
        var id = Guid.NewGuid().ToString("N");
        var error = NewQueue($"test-{id}_error");
        Put(error, "first", "m1");
        Put(error, "second", "m2");

        var act = () => CreateClient().MoveAllAsync(error, $"test-{id}-no-such-destination");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not exist*");
        // nothing was lost: both messages are still in the error queue (the failed one was
        // returned to it when its channel closed)
        await WaitUntil(() => Channel.MessageCount(error) == 2);
        Channel.MessageCount(error).Should().Be(2);
    }

    [RequiresRabbitMqFact]
    public async Task WhenTheBrokerIsUnreachable_ThrowsBrokerUnavailable()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "127.0.0.1",
            ["RabbitMQ:Port"] = "1",           // nothing listens here
            ["RabbitMQ:Username"] = "x",
            ["RabbitMQ:Password"] = "x"
        }).Build();
        var client = new RabbitMqErrorQueueClient(configuration, NullLogger<RabbitMqErrorQueueClient>.Instance);

        await ((Func<Task>)(() => client.GetStatusAsync(["any_error"])))
            .Should().ThrowAsync<BrokerUnavailableException>();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
    }
}
