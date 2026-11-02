using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The figure behind the DeadLettersPresent alert, end to end on a real RabbitMQ: a message that its consumer
// cannot process goes through the production retry policy into the real <endpoint>_error queue, the monitor
// reads that queue over AMQP and exports it as shopeasy_error_queue_messages, and requeueing it brings the
// figure back to 0. See InfrastructureFacts.cs for how to run these.
[Collection(MetricsCollection.Name)]
public sealed class DeadLetterRabbitMqTests : IDisposable
{
    public record Poison { public Guid Id { get; init; } }

    // The consumer fails for as long as the test says so, then handles the message
    private sealed class Cause
    {
        private int _attempts;
        public volatile bool Failing = true;
        public int Attempts => _attempts;
        public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hit()
        {
            Interlocked.Increment(ref _attempts);
            if (Failing) throw new ApplicationException("the cause the test removes later");
            Handled.TrySetResult();
        }
    }

    private sealed class PoisonConsumer(Cause cause) : IConsumer<Poison>
    {
        public Task Consume(ConsumeContext<Poison> context)
        {
            cause.Hit();
            return Task.CompletedTask;
        }
    }

    private readonly string _queue = $"dlq-gauge-{Guid.NewGuid():N}";
    private string ErrorQueue => _queue + ErrorQueues.Suffix;
    private string NeverFailedQueue => $"{_queue}-other{ErrorQueues.Suffix}"; // MassTransit never created it
    private string Series(string queue) => $"shopeasy_error_queue_messages{{queue=\"{queue}\"}}";

    private static IConfiguration Settings()
    {
        var uri = new Uri(TestInfrastructure.RabbitMq!);
        var credentials = uri.UserInfo.Split(':', 2);
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = uri.Host,
            ["RabbitMQ:Port"] = (uri.Port > 0 ? uri.Port : 5672).ToString(),
            ["RabbitMQ:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMQ:Password"] = Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? "")
        }).Build();
    }

    private static async Task<bool> BecomesTrue(Func<Task<bool>> condition, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(200);
        }
        return await condition();
    }

    [RequiresRabbitMqFact]
    public async Task AMessageThatFailsItsRetries_IsOnTheGaugeAsOne_AndRequeueingItBringsItBackToZero()
    {
        var cause = new Cause();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(cause);
        services.AddMassTransit(x =>
        {
            x.AddConsumer<PoisonConsumer>();
            x.UsingRabbitMq((ctx, cfg) =>
            {
                var rabbit = Settings();
                cfg.Host(rabbit["RabbitMQ:Host"], ushort.Parse(rabbit["RabbitMQ:Port"]!), "/", h =>
                {
                    h.Username(rabbit["RabbitMQ:Username"]!);
                    h.Password(rabbit["RabbitMQ:Password"]!);
                });
                // the production policy: three quick retries for an ordinary failure, then the error queue
                cfg.UseConsumerResilience(TestResilience.FastConsumers());
                cfg.ReceiveEndpoint(_queue, e =>
                {
                    e.ConfigureConsumeTopology = false; // only this queue, no exchange for the message type left behind
                    e.ConfigureConsumer<PoisonConsumer>(ctx);
                });
            });
        });

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var bus = provider.GetRequiredService<IBusControl>();
        await TestBus.Bounded(() => bus.StartAsync(), "start", TestBus.Limit);
        try
        {
            var client = new RabbitMqErrorQueueClient(Settings(), NullLogger<RabbitMqErrorQueueClient>.Instance);
            var monitor = new DeadLetterMonitor(client, Settings(), NullLogger<DeadLetterMonitor>.Instance)
            {
                Queues = [ErrorQueue, NeverFailedQueue]
            };

            // the message is sent, fails, is retried and is parked in the error queue
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{_queue}"));
            await endpoint.Send(new Poison { Id = Guid.NewGuid() });

            (await BecomesTrue(async () =>
            {
                await monitor.CheckErrorQueues(CancellationToken.None);
                return await MetricValues.ReadAsync(Series(ErrorQueue)) == 1;
            })).Should().BeTrue("the failed message must reach the gauge");
            cause.Attempts.Should().Be(4, "one attempt and the three quick retries");
            (await MetricValues.ExistsAsync(Series(NeverFailedQueue))).Should().BeTrue();
            (await MetricValues.ReadAsync(Series(NeverFailedQueue))).Should().Be(0, "a queue nothing failed in is 0, not a gap");

            // the cause is fixed and the message is requeued, as the Dead Letter Monitor page does
            cause.Failing = false;
            var moved = await client.MoveAllAsync(ErrorQueue, _queue);
            moved.Should().Be(new MoveResult(MoveStatus.Moved, 1));
            await cause.Handled.Task.WaitAsync(TimeSpan.FromSeconds(30));

            (await BecomesTrue(async () =>
            {
                await monitor.CheckErrorQueues(CancellationToken.None);
                return await MetricValues.ReadAsync(Series(ErrorQueue)) == 0;
            })).Should().BeTrue("the gauge must go back to 0 once the queue is empty");
        }
        finally
        {
            await TestBus.Bounded(() => bus.StopAsync(), "stop", TestBus.Limit);
        }
    }

    // No broker needed: the real client against a port nobody listens on
    [Fact]
    public async Task WhenRabbitMqCannotBeReached_ThePassFails_AndNothingIsReportedAsZero()
    {
        var unreachable = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "127.0.0.1",
            ["RabbitMQ:Port"] = "1", // nothing listens here
            ["RabbitMQ:Username"] = "guest",
            ["RabbitMQ:Password"] = "guest"
        }).Build();
        var client = new RabbitMqErrorQueueClient(unreachable, NullLogger<RabbitMqErrorQueueClient>.Instance);
        var monitor = new DeadLetterMonitor(client, unreachable, NullLogger<DeadLetterMonitor>.Instance)
        {
            Queues = [ErrorQueue]
        };

        var act = () => monitor.CheckErrorQueues(CancellationToken.None);

        await act.Should().ThrowAsync<BrokerUnavailableException>();
        (await MetricValues.ExistsAsync(Series(ErrorQueue))).Should().BeFalse();
    }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(TestInfrastructure.RabbitMq)) return;
        try
        {
            using var connection = new ConnectionFactory { Uri = new Uri(TestInfrastructure.RabbitMq) }.CreateConnection();
            using var channel = connection.CreateModel();
            foreach (var queue in new[] { _queue, ErrorQueue })
            {
                try { channel.QueueDelete(queue); } catch { /* never created */ }
                try { channel.ExchangeDelete(queue); } catch { /* never created */ }
            }
        }
        catch
        {
            // the broker is gone; nothing to clean up
        }
    }
}
