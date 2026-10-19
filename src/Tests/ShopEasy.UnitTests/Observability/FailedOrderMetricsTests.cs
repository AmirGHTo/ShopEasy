using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// An order the orchestrator cannot move forward is retried, and counted as failed only when it gives up:
// that is the order a person has to look at.
[Collection(MetricsCollection.Name)]
public class FailedOrderMetricsTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    // one polling pass over a saga whose first command cannot be published
    private async Task<OrderSagaState> PollWithBrokenBus(int retriesSoFar)
    {
        var orderId = Guid.NewGuid();
        _db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = SagaStatus.Started, UserId = "u", UserEmail = "u@test.com",
            TotalAmount = 20m, ItemsJson = "[]", RetryCount = retriesSoFar, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await _db.SaveChangesAsync();
        var bus = Substitute.For<IPublishEndpoint>();
        bus.Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker is down"));
        var services = new ServiceCollection()
            .AddSingleton(_db).AddSingleton(bus).AddSingleton<ISagaLocking>(new InMemorySagaLocking()).BuildServiceProvider();

        await new SagaOrchestrator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance,
                Options.Create(new SagaOrchestratorOptions { MaxAttempts = 3 }))
            .ProcessPendingSagas(CancellationToken.None);

        return await _db.SagaStates.SingleAsync(s => s.OrderId == orderId);
    }

    private static Task<double> Failed() =>
        MetricValues.ReadAsync("shopeasy_order_outcomes_total{outcome=\"failed\",reason=\"orchestrator_error\"}");

    [Fact]
    public async Task AnOrderTheOrchestratorGivesUpOn_IsCountedAsFailed()
    {
        var before = await Failed();

        (await PollWithBrokenBus(retriesSoFar: 2)).Status.Should().Be(SagaStatus.Failed);

        (await Failed()).Should().Be(before + 1);
    }

    [Fact]
    public async Task AnOrderStillBeingRetried_IsNotCountedYet()
    {
        var before = await Failed();

        (await PollWithBrokenBus(retriesSoFar: 0)).Status.Should().Be(SagaStatus.Started);

        (await Failed()).Should().Be(before);
    }
}
