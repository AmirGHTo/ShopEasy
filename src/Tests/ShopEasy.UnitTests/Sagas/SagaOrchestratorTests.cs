using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using System.Text.Json;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

public class SagaOrchestratorTests : IDisposable
{
    private readonly OrderDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;

    public SagaOrchestratorTests()
    {
        // Fresh in-memory database per test. The consumers open transactions,
        // which the InMemory provider does not support, so ignore that warning.
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new OrderDbContext(options);
        _db.Database.EnsureCreated();

        _publishEndpoint = Substitute.For<IPublishEndpoint>();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // ── OrderConfirmedSagaStarter Tests ───────────────────────────────────────

    [Fact]
    public async Task SagaStarter_WhenOrderConfirmed_CreatesSagaWithStartedStatus()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var context = CreateConsumeContext(new OrderConfirmedEvent
        {
            OrderId = orderId,
            UserId = "user-123",
            UserEmail = "test@test.com",
            TotalAmount = 99.99m,
            ConfirmedAt = DateTime.UtcNow,
            Items = new List<OrderLineItem>()
        });

        var consumer = new OrderConfirmedSagaStarter(
            _db,
            NullLogger<OrderConfirmedSagaStarter>.Instance);

        // Act
        await consumer.Consume(context);

        // Assert
        var saga = await _db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == orderId);
        saga.Should().NotBeNull();
        saga!.Status.Should().Be(SagaStatus.Started);
        saga.UserId.Should().Be("user-123");
        saga.TotalAmount.Should().Be(99.99m);
    }

    [Fact]
    public async Task SagaStarter_WhenDuplicateEvent_DoesNotCreateDuplicateSaga()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var evt = new OrderConfirmedEvent
        {
            OrderId = orderId,
            UserId = "user-123",
            UserEmail = "test@test.com",
            TotalAmount = 99.99m,
            ConfirmedAt = DateTime.UtcNow,
            Items = new List<OrderLineItem>()
        };

        var consumer = new OrderConfirmedSagaStarter(
            _db,
            NullLogger<OrderConfirmedSagaStarter>.Instance);

        // Act — consume the same event twice
        await consumer.Consume(CreateConsumeContext(evt));
        await consumer.Consume(CreateConsumeContext(evt));

        // Assert — only one saga created
        var sagas = await _db.SagaStates
            .Where(s => s.OrderId == orderId)
            .ToListAsync();

        sagas.Should().HaveCount(1);
    }

    // ── InventoryReservedConsumer Tests ───────────────────────────────────────

    [Fact]
    public async Task InventoryReserved_WhenSagaInReservingState_MovesToPaymentProcessing()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
        _db.SagaStates.Add(saga);
        await _db.SaveChangesAsync();

        var context = CreateConsumeContext(new InventoryReservedEvent { OrderId = orderId });
        var consumer = new InventoryReservedConsumer(
            _db,
            _publishEndpoint,
            NullLogger<InventoryReservedConsumer>.Instance);

        // Act
        await consumer.Consume(context);

        // Assert
        var updated = await _db.SagaStates.FindAsync(saga.Id);
        updated!.Status.Should().Be(SagaStatus.ProcessingPayment);

        // Verify ProcessPaymentCommand was published
        await _publishEndpoint.Received(1).Publish(
            Arg.Is<ProcessPaymentCommand>(c => c.OrderId == orderId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InventoryReserved_WhenSagaNotFound_DoesNothing()
    {
        // Arrange
        var context = CreateConsumeContext(
            new InventoryReservedEvent { OrderId = Guid.NewGuid() });
        var consumer = new InventoryReservedConsumer(
            _db,
            _publishEndpoint,
            NullLogger<InventoryReservedConsumer>.Instance);

        // Act — should not throw
        await consumer.Consume(context);

        // Assert — no commands published
        await _publishEndpoint.DidNotReceive().Publish(
            Arg.Any<ProcessPaymentCommand>(),
            Arg.Any<CancellationToken>());
    }

    // ── PaymentSucceededConsumer Tests ────────────────────────────────────────

    //[Fact]
    //public async Task PaymentSucceeded_WhenSagaInPaymentProcessing_MovesToCompleting()
    //{
    //    // Arrange
    //    var orderId = Guid.NewGuid();
    //    var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
    //    _db.SagaStates.Add(saga);
    //    await _db.SaveChangesAsync();

    //    var context = CreateConsumeContext(new PaymentSucceededEvent
    //    {
    //        OrderId = orderId,
    //        PaymentId = Guid.NewGuid(),
    //        Amount = 99.99m
    //    });
    //    var consumer = new PaymentSucceededConsumer(
    //        _db,
    //        _publishEndpoint,
    //        NullLogger<PaymentSucceededConsumer>.Instance);

    //    // Act
    //    await consumer.Consume(context);

    //    // Assert
    //    var updated = await _db.SagaStates.FindAsync(saga.Id);
    //    updated!.Status.Should().Be(SagaStatus.Completing);

    //    // Verify CommitInventoryCommand was published
    //    await _publishEndpoint.Received(1).Publish(
    //        Arg.Is<CommitInventoryCommand>(c => c.OrderId == orderId),
    //        Arg.Any<CancellationToken>());
    //}

    // ── PaymentFailedConsumer Tests ───────────────────────────────────────────

    //[Fact]
    //public async Task PaymentFailed_WhenSagaInPaymentProcessing_MovesToReleasingInventory()
    //{
    //    // Arrange
    //    var orderId = Guid.NewGuid();
    //    var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
    //    _db.SagaStates.Add(saga);
    //    await _db.SaveChangesAsync();

    //    var context = CreateConsumeContext(new PaymentFailedEvent
    //    {
    //        OrderId = orderId,
    //        Reason = "Insufficient funds"
    //    });
    //    var consumer = new PaymentFailedConsumer(
    //        _db,
    //        _publishEndpoint,
    //        NullLogger<PaymentFailedConsumer>.Instance);

    //    // Act
    //    await consumer.Consume(context);

    //    // Assert
    //    var updated = await _db.SagaStates.FindAsync(saga.Id);
    //    updated!.Status.Should().Be(SagaStatus.ReleasingInventory);
    //    updated.FailureReason.Should().Be("Insufficient funds");

    //    // Verify ReleaseInventoryCommand was published (compensating action)
    //    await _publishEndpoint.Received(1).Publish(
    //        Arg.Is<ReleaseInventoryCommand>(c => c.OrderId == orderId),
    //        Arg.Any<CancellationToken>());
    //}

    //[Fact]
    //public async Task PaymentFailed_SagaFailureReasonIsRecorded()
    //{
    //    // Arrange
    //    var orderId = Guid.NewGuid();
    //    var failureReason = "Payment declined by issuing bank";
    //    var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
    //    _db.SagaStates.Add(saga);
    //    await _db.SaveChangesAsync();

    //    var consumer = new PaymentFailedConsumer(
    //        _db,
    //        _publishEndpoint,
    //        NullLogger<PaymentFailedConsumer>.Instance);

    //    // Act
    //    await consumer.Consume(CreateConsumeContext(new PaymentFailedEvent
    //    {
    //        OrderId = orderId,
    //        Reason = failureReason
    //    }));

    //    // Assert
    //    var updated = await _db.SagaStates.FindAsync(saga.Id);
    //    updated!.FailureReason.Should().Be(failureReason);
    //}

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrderSagaState CreateSaga(Guid orderId, SagaStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Status = status,
            UserId = "user-123",
            UserEmail = "test@test.com",
            TotalAmount = 99.99m,
            ItemsJson = JsonSerializer.Serialize(new List<OrderLineItem>()),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static ConsumeContext<T> CreateConsumeContext<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }
}