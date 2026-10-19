using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

// When the orchestrator cannot send a saga's first command it tries again after a growing pause, and
// only after MaxAttempts failures gives up: the saga is Failed and the order cancelled, because the
// timeout service skips Failed sagas and nothing else would end it.
public class OrchestratorRetryTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private readonly IPublishEndpoint _bus = Substitute.For<IPublishEndpoint>();

    public void Dispose() => _db.Dispose();

    // one polling pass over an order whose saga cannot publish its first command; its last attempt
    // was `lastAttemptAgo` ago (long ago unless the test is about the pause)
    private async Task<Guid> PollWithBrokenBus(
        int retriesSoFar, OrderStatus orderStatus = OrderStatus.Pending, int maxAttempts = 3, TimeSpan? lastAttemptAgo = null)
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var lastAttempt = now - (lastAttemptAgo ?? TimeSpan.FromHours(1));
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
            TotalAmount = 20m, ItemsJson = "[]", RetryCount = retriesSoFar, CreatedAt = now, UpdatedAt = lastAttempt
        });
        await _db.SaveChangesAsync();

        _bus.Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker is down"));
        var services = new ServiceCollection()
            .AddSingleton(_db).AddSingleton(_bus).AddSingleton<ISagaLocking>(new InMemorySagaLocking()).BuildServiceProvider();

        await new SagaOrchestrator(
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance,
                Options.Create(new SagaOrchestratorOptions { MaxAttempts = maxAttempts }))
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

    [Fact]
    public async Task ASagaThatHasFailedThreeTimes_IsNotGivenUpOnWhileMoreAttemptsAreAllowed()
    {
        var orderId = await PollWithBrokenBus(retriesSoFar: 2, maxAttempts: 8);

        var saga = await _db.SagaStates.SingleAsync(s => s.OrderId == orderId);
        saga.Status.Should().Be(SagaStatus.Started);
        saga.RetryCount.Should().Be(3);
        (await _db.Orders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task ASagaStillInItsPause_IsNotTried()
    {
        var orderId = await PollWithBrokenBus(retriesSoFar: 1, lastAttemptAgo: TimeSpan.FromSeconds(1));

        await _bus.DidNotReceive().Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>());
        (await _db.SagaStates.SingleAsync(s => s.OrderId == orderId)).RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task ASagaWhosePauseIsOver_IsTriedAgain()
    {
        await PollWithBrokenBus(retriesSoFar: 1, lastAttemptAgo: TimeSpan.FromMinutes(1));

        await _bus.Received(1).Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ThePausesGrow_AndStopAtTheMaximum()
    {
        var options = new SagaOrchestratorOptions();

        Enumerable.Range(1, 7).Select(n => options.PauseAfter(n).TotalSeconds)
            .Should().Equal(3, 6, 12, 24, 48, 60, 60);
    }
}
