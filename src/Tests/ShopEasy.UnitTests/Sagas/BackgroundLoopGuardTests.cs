using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public async Task Shutdown_is_not_reported_as_a_failure()
    {
        using var stopping = new CancellationTokenSource();
        var log = new ListLogger<SagaOrchestrator>();
        stopping.Cancel();

        var act = () => GuardedPass.RunAsync(
            () => Task.FromCanceled(stopping.Token), log, "Saga orchestrator", stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Messages(LogLevel.Error).Should().BeEmpty();
    }
}
