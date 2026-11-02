using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

public class ErrorQueuesTests
{
    [Theory]
    [InlineData("payment-process_error", "payment-process")]
    [InlineData("order-saga-inventory-commit-failed_error", "order-saga-inventory-commit-failed")]
    // only the trailing suffix is removed — a name that merely contains "_error" is left alone
    [InlineData("a_error_b_error", "a_error_b")]
    public void OriginalQueueOf_RemovesOnlyTheTrailingSuffix(string errorQueue, string expected)
        => ErrorQueues.OriginalQueueOf(errorQueue).Should().Be(expected);

    [Fact]
    public void EveryMonitoredQueueIsAnErrorQueue()
        => ErrorQueues.Monitored.Should().OnlyContain(q => q.EndsWith(ErrorQueues.Suffix));

    [Theory]
    [InlineData("payment-process_error", true)]
    [InlineData("payment-process", false)]            // the live queue is not an error queue
    [InlineData("some-other-service_error", false)]   // not one of ours
    [InlineData("", false)]
    public void IsMonitored_OnlyAcceptsTheKnownErrorQueues(string name, bool expected)
        => ErrorQueues.IsMonitored(name).Should().Be(expected);
}

public class DeadLetterMonitorTests
{
    private static DeadLetterMonitor Create(IErrorQueueClient client, out ListLogger<DeadLetterMonitor> log,
        Dictionary<string, string?>? config = null)
    {
        log = new ListLogger<DeadLetterMonitor>();
        return new DeadLetterMonitor(
            client, new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(), log);
    }

    [Fact]
    public async Task AlertsForEveryQueueThatHoldsFailedMessages_AndOnlyThose()
    {
        var client = Substitute.For<IErrorQueueClient>();
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErrorQueueStatus>
            {
                new("payment-process_error", 3, 0),
                new("inventory-reserve_error", 0, 0),
                new("order-saga-starter_error", 1, 0)
            });
        var monitor = Create(client, out var log);

        await monitor.CheckErrorQueues(CancellationToken.None);

        var alerts = log.Messages(LogLevel.Error);
        alerts.Should().HaveCount(2);
        alerts.Should().Contain(m => m.Contains("payment-process_error") && m.Contains("3 failed"));
        alerts.Should().Contain(m => m.Contains("order-saga-starter_error"));
    }

    [Fact]
    public async Task WhenTheBrokerIsDown_TheCheckFails_SoThePassIsCountedAsFailed()
    {
        var client = Substitute.For<IErrorQueueClient>();
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ErrorQueueStatus>>>(
                _ => throw new BrokerUnavailableException("RabbitMQ is not reachable."));
        var monitor = Create(client, out var log);

        // the guard around the loop counts it and logs the outage once; the monitor must not hide it
        var act = () => monitor.CheckErrorQueues(CancellationToken.None);

        await act.Should().ThrowAsync<BrokerUnavailableException>();
        log.Messages(LogLevel.Error).Should().BeEmpty("a queue holding messages is the only thing it alerts on");
    }

    [Fact]
    public async Task ManagementLinkIsOnlyIncludedWhenConfigured()
    {
        var client = Substitute.For<IErrorQueueClient>();
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErrorQueueStatus> { new("payment-process_error", 1, 0) });

        var without = await Create(client, out _).GetAllErrorQueues();
        without.Single().ManagementUrl.Should().BeNull();

        var with = await Create(client, out _, new() { ["RabbitMQ:ManagementUiUrl"] = "https://mq.example.com/" })
            .GetAllErrorQueues();
        with.Single().ManagementUrl.Should().Be("https://mq.example.com/#/queues/%2F/payment-process_error");
    }
}

// shopeasy_error_queue_messages, the figure behind the DeadLettersPresent alert. The metric is static and
// shared by the whole run, so every test uses queue names of its own.
[Collection(MetricsCollection.Name)]
public class DeadLetterGaugeTests
{
    private static string Series(string queue) => $"shopeasy_error_queue_messages{{queue=\"{queue}\"}}";

    private static string NewQueue(string name) => $"{name}-{Guid.NewGuid():N}_error";

    private static DeadLetterMonitor Create(IErrorQueueClient client, params string[] queues) =>
        new(client, new ConfigurationBuilder().Build(), NullLogger<DeadLetterMonitor>.Instance)
        {
            Queues = queues
        };

    private static IErrorQueueClient Broker(params ErrorQueueStatus[] statuses)
    {
        var client = Substitute.For<IErrorQueueClient>();
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(statuses.ToList());
        return client;
    }

    [Fact]
    public async Task EveryMonitoredQueueHasAValue_ZeroWhenItIsEmptyOrDoesNotExistYet()
    {
        var holding = NewQueue("holding");
        var empty = NewQueue("empty");
        var missing = NewQueue("missing"); // MassTransit creates an error queue when its first message fails
        var monitor = Create(Broker(new(holding, 3, 0), new(empty, 0, 0)), holding, empty, missing);

        await monitor.CheckErrorQueues(CancellationToken.None);

        (await MetricValues.ReadAsync(Series(holding))).Should().Be(3);
        (await MetricValues.ExistsAsync(Series(empty))).Should().BeTrue();
        (await MetricValues.ReadAsync(Series(empty))).Should().Be(0);
        (await MetricValues.ExistsAsync(Series(missing))).Should().BeTrue("\"nothing failed\" is a value, not a gap");
        (await MetricValues.ReadAsync(Series(missing))).Should().Be(0);
    }

    [Fact]
    public async Task ARequeuedQueue_GoesBackToZero()
    {
        var queue = NewQueue("requeued");
        var client = Substitute.For<IErrorQueueClient>();
        var monitor = Create(client, queue);

        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErrorQueueStatus> { new(queue, 2, 0) });
        await monitor.CheckErrorQueues(CancellationToken.None);
        (await MetricValues.ReadAsync(Series(queue))).Should().Be(2);

        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErrorQueueStatus> { new(queue, 0, 0) });
        await monitor.CheckErrorQueues(CancellationToken.None);

        (await MetricValues.ReadAsync(Series(queue))).Should().Be(0);
    }

    [Fact]
    public async Task APassThatCannotReachTheBroker_ExportsNothing_NotZero()
    {
        var queue = NewQueue("never-read");
        var client = Substitute.For<IErrorQueueClient>();
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ErrorQueueStatus>>>(_ => throw new BrokerUnavailableException("down"));

        var act = () => Create(client, queue).CheckErrorQueues(CancellationToken.None);

        await act.Should().ThrowAsync<BrokerUnavailableException>();
        (await MetricValues.ExistsAsync(Series(queue))).Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheBrokerGoesAway_TheLastValueStays_SoAnOutageNeverLooksLikeAnEmptyQueue()
    {
        var queue = NewQueue("outage");
        var client = Substitute.For<IErrorQueueClient>();
        var monitor = Create(client, queue);
        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErrorQueueStatus> { new(queue, 4, 0) });
        await monitor.CheckErrorQueues(CancellationToken.None);

        client.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ErrorQueueStatus>>>(_ => throw new BrokerUnavailableException("down"));
        var act = () => monitor.CheckErrorQueues(CancellationToken.None);
        await act.Should().ThrowAsync<BrokerUnavailableException>();

        (await MetricValues.ReadAsync(Series(queue))).Should().Be(4);
    }

    [Fact]
    public async Task ByDefault_EveryKnownErrorQueueIsExported()
    {
        var monitor = new DeadLetterMonitor(
            Broker(), new ConfigurationBuilder().Build(), NullLogger<DeadLetterMonitor>.Instance);

        await monitor.CheckErrorQueues(CancellationToken.None);

        foreach (var queue in ErrorQueues.Monitored)
            (await MetricValues.ExistsAsync(Series(queue))).Should().BeTrue(queue);
    }
}

public class OutboxControllerRequeueTests
{
    private static (OutboxController Controller, IErrorQueueClient Client) Create()
    {
        var client = Substitute.For<IErrorQueueClient>();
        var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var monitor = new DeadLetterMonitor(
            client, new ConfigurationBuilder().Build(), NullLogger<DeadLetterMonitor>.Instance);

        return (new OutboxController(db, monitor, client, NullLogger<OutboxController>.Instance), client);
    }

    [Theory]
    [InlineData("payment-process")]            // a live queue
    [InlineData("unknown_error")]              // an error queue that is not ours
    [InlineData("../../etc_error")]
    public async Task RefusesAnythingButTheKnownErrorQueues(string queue)
    {
        var (controller, client) = Create();

        var result = await controller.Requeue(queue, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        await client.DidNotReceiveWithAnyArgs().MoveAllAsync(default!, default!, default);
    }

    [Fact]
    public async Task MovesTheMessagesBackToTheQueueTheyCameFrom()
    {
        var (controller, client) = Create();
        client.MoveAllAsync("payment-process_error", "payment-process", Arg.Any<CancellationToken>())
            .Returns(new MoveResult(MoveStatus.Moved, 4));

        var result = await controller.Requeue("payment-process_error", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value!.ToString().Should().Contain("moved = 4");
    }

    [Fact]
    public async Task WhenTheErrorQueueDoesNotExist_Returns404()
    {
        var (controller, client) = Create();
        client.MoveAllAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MoveResult(MoveStatus.QueueNotFound, 0));

        (await controller.Requeue("payment-process_error", CancellationToken.None))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task WhenTheBrokerIsUnreachable_Returns503()
    {
        var (controller, client) = Create();
        client.MoveAllAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<MoveResult>>(_ => throw new BrokerUnavailableException("down"));

        var result = await controller.Requeue("payment-process_error", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }
}
