using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Sagas;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

// A database error in one polling pass must not end the loop: an exception that leaves ExecuteAsync
// stops the whole Order host, and with it every order in flight.
public class BackgroundLoopGuardTests
{
    private static IServiceScopeFactory BrokenDatabase()
    {
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(_ => throw new InvalidOperationException("database is restarting"));
        return factory;
    }

    // starts the service, waits for its first pass to fail, stops it
    private static async Task<(Task Loop, ListLogger<T> Log)> RunFirstPass<T>(
        Func<IServiceScopeFactory, ListLogger<T>, BackgroundService> create) where T : class
    {
        var log = new ListLogger<T>();
        var service = create(BrokenDatabase(), log);
        await service.StartAsync(CancellationToken.None);

        var stopAt = DateTime.UtcNow.AddSeconds(5);
        while (log.Messages(LogLevel.Error).Count == 0 && !service.ExecuteTask!.IsCompleted && DateTime.UtcNow < stopAt)
            await Task.Delay(10);

        var loop = service.ExecuteTask!;
        var stillRunning = !loop.IsCompleted;
        await service.StopAsync(CancellationToken.None);
        if (!stillRunning) await loop; // surfaces the exception that ended the loop
        return (loop, log);
    }

    [Fact]
    public async Task The_orchestrator_logs_a_failed_pass_and_keeps_running()
    {
        var (loop, log) = await RunFirstPass<SagaOrchestrator>((scopes, l) => new SagaOrchestrator(scopes, l));

        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Saga orchestrator pass failed");
        loop.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task The_timeout_service_logs_a_failed_pass_and_keeps_running()
    {
        var (loop, log) = await RunFirstPass<SagaTimeoutService>((scopes, l) => new SagaTimeoutService(scopes, l));

        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Saga timeout service pass failed");
        loop.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task The_outbox_delivery_service_logs_a_failed_pass_and_keeps_running()
    {
        var (loop, log) = await RunFirstPass<OutboxDeliveryService>(
            (scopes, l) => new OutboxDeliveryService(scopes, Substitute.For<IBrokerReadiness>(), l));

        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Outbox delivery service pass failed");
        loop.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task The_dead_letter_monitor_logs_a_failed_pass_and_keeps_running()
    {
        // the monitor handles an unreachable broker itself; this is a failure outside that call
        var queues = Substitute.For<IErrorQueueClient>();
        queues.GetStatusAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ErrorQueueStatus>>(null!));

        var (loop, log) = await RunFirstPass<DeadLetterMonitor>(
            (_, l) => new DeadLetterMonitor(queues, Substitute.For<IConfiguration>(), l));

        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Dead letter monitor pass failed");
        loop.IsFaulted.Should().BeFalse();
    }

    private static GuardedPass Guard(ListLogger<SagaOrchestrator> log, int passTimeoutSeconds = 120) =>
        new(log, "Saga orchestrator", Options.Create(new BackgroundLoopOptions { PassTimeoutSeconds = passTimeoutSeconds }));

    private static Task Fail(GuardedPass guard) =>
        guard.RunAsync(_ => throw new InvalidOperationException("database is restarting"), CancellationToken.None);

    [Fact]
    public async Task An_outage_is_logged_as_a_few_lines_not_one_per_pass()
    {
        var log = new ListLogger<SagaOrchestrator>();
        var guard = Guard(log);

        for (var i = 0; i < 9; i++) await Fail(guard);

        // the first failure, then each time the number of failed passes in a row doubles: 2, 4, 8
        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("Saga orchestrator pass failed");
        log.Messages(LogLevel.Warning).Should().HaveCount(3)
            .And.Contain(m => m.Contains("2 passes in a row") && m.Contains("database is restarting"))
            .And.Contain(m => m.Contains("8 passes in a row"));
    }

    [Fact]
    public async Task A_pass_that_succeeds_ends_the_outage_and_the_next_failure_is_reported_afresh()
    {
        var log = new ListLogger<SagaOrchestrator>();
        var guard = Guard(log);

        await Fail(guard);
        await Fail(guard);
        await guard.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await Fail(guard);

        log.Messages(LogLevel.Information).Should().ContainSingle().Which.Should().Contain("recovered after 2 failed pass");
        log.Messages(LogLevel.Error).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_pass_that_hangs_is_cancelled_and_counts_as_a_failure()
    {
        var log = new ListLogger<SagaOrchestrator>();
        var guard = Guard(log, passTimeoutSeconds: 1);

        // a pass that waits for ever, like a connection that never answers
        await guard.RunAsync(token => Task.Delay(Timeout.Infinite, token), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        log.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain("pass failed");
    }

    [Fact]
    public async Task Shutdown_is_not_reported_as_a_failure()
    {
        using var stopping = new CancellationTokenSource();
        var log = new ListLogger<SagaOrchestrator>();
        stopping.Cancel();

        var act = () => Guard(log).RunAsync(Task.FromCanceled, stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Messages(LogLevel.Error).Should().BeEmpty();
    }
}
