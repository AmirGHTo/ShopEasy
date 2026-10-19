using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

// When the orchestrator gives up on a saga (its first command could not be sent three times) the
// order must not stay Pending: the timeout service skips Failed sagas, so nothing else would end it.
public class OrchestratorGiveUpTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    // one polling pass over an order whose saga cannot publish its first command
    private async Task<Guid> PollWithBrokenBus(int retriesSoFar, OrderStatus orderStatus = OrderStatus.Pending)
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _db.Orders.Add(new Order
        {
            Id = orderId, UserId = "u", UserEmail = "u@test.com", Status = orderStatus,
            TotalAmount = 20m, CreatedAt = now, UpdatedAt = now
        });
        _db.OrderSummaries.Add(new OrderSummary
        {
            OrderId = orderId, UserId = "u", UserEmail = "u@test.com", StatusName = orderStatus.ToString(),
            StatusCode = (int)orderStatus, TotalAmount = 20m, AmountDisplay = "$20.00", ItemCount = 2,
            ItemsSummary = "Widget x2", PlacedAt = now, UpdatedAt = now
        });
        _db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = SagaStatus.Started, UserId = "u", UserEmail = "u@test.com",
            TotalAmount = 20m, ItemsJson = "[]", RetryCount = retriesSoFar, CreatedAt = now, UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var bus = Substitute.For<IPublishEndpoint>();
        bus.Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker is down"));
        var services = new ServiceCollection()
            .AddSingleton(_db).AddSingleton(bus).AddSingleton<ISagaLocking>(new InMemorySagaLocking()).BuildServiceProvider();

        await new SagaOrchestrator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance)
            .ProcessPendingSagas(CancellationToken.None);

        return orderId;
    }

    [Fact]
    public async Task AnOrderTheOrchestratorGivesUpOn_IsCancelled_WithAFixedMessageForTheCustomer()
    {
        var orderId = await PollWithBrokenBus(retriesSoFar: 2);

        (await _db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Cancelled);
        var shown = await _db.OrderSummaries.SingleAsync(s => s.OrderId == orderId);
        shown.StatusName.Should().Be("Cancelled");
        shown.FailureReason.Should().Be(FailureReasons.OrderCouldNotBeProcessed, "the exception text is not for the customer");
        var saga = await _db.SagaStates.SingleAsync(s => s.OrderId == orderId);
        saga.Status.Should().Be(SagaStatus.Failed);
        saga.FailureReason.Should().Be("broker is down", "the technical reason stays on the saga");
    }

    [Fact]
    public async Task AnOrderStillBeingRetried_StaysPending()
    {
        var orderId = await PollWithBrokenBus(retriesSoFar: 0);

        (await _db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Pending);
        (await _db.OrderSummaries.SingleAsync(s => s.OrderId == orderId)).StatusName.Should().Be("Pending");
    }

    [Fact]
    public async Task AnOrderThatIsNoLongerPending_IsNotTouched()
    {
        var orderId = await PollWithBrokenBus(retriesSoFar: 2, OrderStatus.Confirmed);

        (await _db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Confirmed);
    }
}
