//using FluentAssertions;
//using MassTransit;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.EntityFrameworkCore.Diagnostics;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging.Abstractions;
//using NSubstitute;
//using ShopEasy.OrderService.Data;
//using ShopEasy.OrderService.Sagas;
//using ShopEasy.OrderService.Services;
//using ShopEasy.Shared.Contracts;
//using System.Text.Json;
//using Xunit;

//namespace ShopEasy.UnitTests.Sagas;

//// ═══════════════════════════════════════════════════════════════════════════
//// SAGA STATE MACHINE TESTS
//// Tests every state transition in the saga orchestrator
//// ═══════════════════════════════════════════════════════════════════════════

//public class SagaStateTransitionTests : IDisposable
//{
//    private readonly OrderDbContext _db;
//    private readonly IPublishEndpoint _publishEndpoint;

//    public SagaStateTransitionTests()
//    {
//        var options = new DbContextOptionsBuilder<OrderDbContext>()
//            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
//            .ConfigureWarnings(w =>
//                w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
//            .Options;

//        _db = new OrderDbContext(options);
//        _publishEndpoint = Substitute.For<IPublishEndpoint>();
//    }


//    // ── Happy Path ──────────────────────────────────────────────────────────

//    [Fact]
//    public async Task HappyPath_OrderConfirmed_CreatesSagaInStartedState()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var consumer = new OrderConfirmedSagaStarter(_db,
//            NullLogger<OrderConfirmedSagaStarter>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new OrderConfirmedEvent
//        {
//            OrderId = orderId,
//            UserId = "user-1",
//            UserEmail = "user@test.com",
//            TotalAmount = 100m,
//            ConfirmedAt = DateTime.UtcNow,
//            Items = new List<OrderLineItem>
//            {
//                new() { ProductId = Guid.NewGuid(), ProductName = "Widget",
//                        Quantity = 2, UnitPrice = 50m }
//            }
//        }));

//        // Assert
//        var saga = await _db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == orderId);
//        saga.Should().NotBeNull();
//        saga!.Status.Should().Be(SagaStatus.Started);
//        saga.TotalAmount.Should().Be(100m);
//        saga.UserId.Should().Be("user-1");
//    }

//    [Fact]
//    public async Task HappyPath_InventoryReserved_TransitionsToProcessingPayment()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReservedConsumer(_db, _publishEndpoint,
//            NullLogger<InventoryReservedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = orderId }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.ProcessingPayment);

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<ProcessPaymentCommand>(c =>
//                c.OrderId == orderId && c.Amount == saga.TotalAmount),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task HappyPath_PaymentSucceeded_TransitionsToCompleting()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentSucceededConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentSucceededConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new PaymentSucceededEvent
//        {
//            OrderId = orderId,
//            PaymentId = Guid.NewGuid(),
//            Amount = 100m
//        }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Completing);

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<CommitInventoryCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task HappyPath_InventoryReleased_TransitionsToCancelled()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReleasingInventory);
//        saga.FailureReason = "Payment declined";
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReleasedConsumer(_db,
//            NullLogger<InventoryReleasedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(
//            new InventoryReleasedEvent { OrderId = orderId }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled);
//    }

//    // ── Payment Failure Compensation ────────────────────────────────────────

//    [Fact]
//    public async Task Compensation_PaymentFailed_PublishesReleaseInventoryCommand()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentFailedConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentFailedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new PaymentFailedEvent
//        {
//            OrderId = orderId,
//            Reason = "Payment declined by issuing bank"
//        }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.ReleasingInventory);
//        updated.FailureReason.Should().Be("Payment declined by issuing bank");

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<ReleaseInventoryCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task Compensation_PaymentFailed_RecordsFailureReason()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var reason = "Amount exceeds single transaction limit";
//        var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentFailedConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentFailedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new PaymentFailedEvent
//        {
//            OrderId = orderId,
//            Reason = reason
//        }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.FailureReason.Should().Be(reason);
//        updated.Status.Should().Be(SagaStatus.ReleasingInventory);
//    }

//    [Fact]
//    public async Task Compensation_InsufficientStock_CancelsSagaWithReason()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReservationFailedConsumer(_db,
//            NullLogger<InventoryReservationFailedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new InventoryReservationFailedEvent
//        {
//            OrderId = orderId,
//            Reason = "Insufficient stock for 4K Webcam"
//        }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled);
//        updated.FailureReason.Should().Contain("Insufficient stock");
//    }

//    [Fact]
//    public async Task Compensation_InsufficientStock_PublishesNothingAndCancelsSaga()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReservationFailedConsumer(_db, NullLogger<InventoryReservationFailedConsumer>.Instance);

//        // Act
//        await consumer.Consume(CreateContext(new InventoryReservationFailedEvent
//        {
//            OrderId = orderId,
//            Reason = "Insufficient stock for 4K Webcam"
//        }));

//        // Assert
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled);
//        updated.FailureReason.Should().Contain("Insufficient stock");

//        // No inventory to release — nothing published
//        await _publishEndpoint.DidNotReceive().Publish(
//            Arg.Any<ReleaseInventoryCommand>(),
//            Arg.Any<CancellationToken>());
//    }

//    // ── Zombie Message Guard (Late Events After Cancellation) ───────────────

//    [Fact]
//    public async Task ZombieGuard_PaymentSucceededForCancelledSaga_PublishesRefund()
//    {
//        // Arrange — saga already cancelled by timeout
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Cancelled);
//        saga.FailureReason = "Timed out waiting for payment";
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentSucceededConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentSucceededConsumer>.Instance);

//        // Act — late payment arrives after cancellation
//        await consumer.Consume(CreateContext(new PaymentSucceededEvent
//        {
//            OrderId = orderId,
//            PaymentId = Guid.NewGuid(),
//            Amount = 100m
//        }));

//        // Assert — saga stays cancelled, refund issued
//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled); // unchanged

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<RefundPaymentCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());

//        // Must NOT have published CommitInventory
//        await _publishEndpoint.DidNotReceive().Publish(
//            Arg.Any<CommitInventoryCommand>(),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task ZombieGuard_PaymentSucceededForFailedSaga_PublishesRefund()
//    {
//        // Same guard should work for Failed status too
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Failed);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentSucceededConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentSucceededConsumer>.Instance);

//        await consumer.Consume(CreateContext(new PaymentSucceededEvent
//        {
//            OrderId = orderId,
//            PaymentId = Guid.NewGuid(),
//            Amount = 100m
//        }));

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Failed); // unchanged

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<RefundPaymentCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task ZombieGuard_PaymentSucceededForCompletedSaga_PublishesRefund()
//    {
//        // Duplicate payment for an already completed order
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Completed);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new PaymentSucceededConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentSucceededConsumer>.Instance);

//        await consumer.Consume(CreateContext(new PaymentSucceededEvent
//        {
//            OrderId = orderId,
//            PaymentId = Guid.NewGuid(),
//            Amount = 100m
//        }));

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Completed); // unchanged

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<RefundPaymentCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task ZombieGuard_InventoryReservedForCancelledSaga_ReleasesReservation()
//    {
//        // Late InventoryReservedEvent arrives after saga was cancelled
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Cancelled);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReservedConsumer(_db, _publishEndpoint,
//            NullLogger<InventoryReservedConsumer>.Instance);

//        await consumer.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = orderId }));

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled); // unchanged

//        // Must release the late reservation
//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<ReleaseInventoryCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());

//        // Must NOT proceed to payment
//        await _publishEndpoint.DidNotReceive().Publish(
//            Arg.Any<ProcessPaymentCommand>(),
//            Arg.Any<CancellationToken>());
//    }

//    // ── Idempotency ─────────────────────────────────────────────────────────

//    [Fact]
//    public async Task Idempotency_DuplicateOrderConfirmed_CreatesSagaOnlyOnce()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var evt = new OrderConfirmedEvent
//        {
//            OrderId = orderId,
//            UserId = "user-1",
//            UserEmail = "user@test.com",
//            TotalAmount = 100m,
//            ConfirmedAt = DateTime.UtcNow,
//            Items = new List<OrderLineItem>()
//        };

//        var consumer = new OrderConfirmedSagaStarter(_db,
//            NullLogger<OrderConfirmedSagaStarter>.Instance);

//        // Act — consume same event twice
//        await consumer.Consume(CreateContext(evt));
//        await consumer.Consume(CreateContext(evt));

//        // Assert — only one saga created
//        var sagas = await _db.SagaStates
//            .Where(s => s.OrderId == orderId).ToListAsync();
//        sagas.Should().HaveCount(1);
//    }

//    [Fact]
//    public async Task Idempotency_DuplicateInventoryReserved_ProcessesOnlyOnce()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var consumer = new InventoryReservedConsumer(_db, _publishEndpoint,
//            NullLogger<InventoryReservedConsumer>.Instance);

//        // Act — consume same event twice
//        await consumer.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = orderId }));
//        await consumer.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = orderId }));

//        // Assert — ProcessPaymentCommand published only once
//        await _publishEndpoint.Received(1).Publish(
//            Arg.Any<ProcessPaymentCommand>(),
//            Arg.Any<CancellationToken>());
//    }

//    // ── Edge Cases ──────────────────────────────────────────────────────────

//    [Fact]
//    public async Task EdgeCase_EventForNonExistentSaga_DoesNothing()
//    {
//        var consumer = new InventoryReservedConsumer(_db, _publishEndpoint,
//            NullLogger<InventoryReservedConsumer>.Instance);

//        // Act — event for unknown order
//        await consumer.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = Guid.NewGuid() }));

//        // Assert — no commands published, no exceptions
//        await _publishEndpoint.DidNotReceive().Publish(
//            Arg.Any<object>(), Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task EdgeCase_PaymentFailedForNonExistentSaga_DoesNothing()
//    {
//        var consumer = new PaymentFailedConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentFailedConsumer>.Instance);

//        await consumer.Consume(CreateContext(new PaymentFailedEvent
//        {
//            OrderId = Guid.NewGuid(),
//            Reason = "Declined"
//        }));

//        await _publishEndpoint.DidNotReceive().Publish(
//            Arg.Any<object>(), Arg.Any<CancellationToken>());
//    }

//    [Fact]
//    public async Task EdgeCase_ZeroAmountOrder_IsHandledCorrectly()
//    {
//        var orderId = Guid.NewGuid();
//        var consumer = new OrderConfirmedSagaStarter(_db,
//            NullLogger<OrderConfirmedSagaStarter>.Instance);

//        await consumer.Consume(CreateContext(new OrderConfirmedEvent
//        {
//            OrderId = orderId,
//            UserId = "user-1",
//            UserEmail = "user@test.com",
//            TotalAmount = 0m,
//            ConfirmedAt = DateTime.UtcNow,
//            Items = new List<OrderLineItem>()
//        }));

//        var saga = await _db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == orderId);
//        saga.Should().NotBeNull();
//        saga!.TotalAmount.Should().Be(0m);
//    }

//    // ── Saga Timeout Tests ──────────────────────────────────────────────────
//    [Fact]
//    public async Task Timeout_SagaStuckInReservingInventory_GetsDetected()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(3);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Failed);
//        updated.FailureReason.Should().Contain("Timed out");
//    }

//    [Fact]
//    public async Task Timeout_SagaStuckInProcessingPayment_ReleasesInventory()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(3);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<ReleaseInventoryCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.ReleasingInventory);
//        updated.FailureReason.Should().Contain("Timed out waiting for payment");
//    }

//    [Fact]
//    public async Task Timeout_SagaStuckInReleasingInventory_ForcesCancelled()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReleasingInventory);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(3);
//        saga.FailureReason = "Payment declined";
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Failed);
//        updated.FailureReason.Should().Contain("Timed out during inventory release");
//    }

//    [Fact]
//    public async Task Timeout_CompletedSaga_IsNotAffectedByTimeout()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Completed);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Completed);
//    }

//    [Fact]
//    public async Task Timeout_CancelledSaga_IsNotAffectedByTimeout()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.Cancelled);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.Cancelled);
//    }

//    [Fact]
//    public async Task Timeout_FreshSaga_IsNotTimedOut()
//    {
//        var orderId = Guid.NewGuid();
//        var saga = CreateSaga(orderId, SagaStatus.ReservingInventory);
//        saga.UpdatedAt = DateTime.UtcNow - TimeSpan.FromSeconds(30);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        var svc = new SagaTimeoutService(
//            Substitute.For<IServiceScopeFactory>(),
//            NullLogger<SagaTimeoutService>.Instance);

//        await svc.CheckForTimedOutSagasPublic(_db, _publishEndpoint, CancellationToken.None);

//        var updated = await _db.SagaStates.FindAsync(saga.Id);
//        updated!.Status.Should().Be(SagaStatus.ReservingInventory);
//    }

//    // ── State Machine Completeness Tests ────────────────────────────────────

//    [Fact]
//    public async Task StateMachine_FullHappyPath_AllTransitionsCorrect()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();

//        // Step 1: Order confirmed → saga started
//        var starter = new OrderConfirmedSagaStarter(_db,
//            NullLogger<OrderConfirmedSagaStarter>.Instance);
//        await starter.Consume(CreateContext(new OrderConfirmedEvent
//        {
//            OrderId = orderId,
//            UserId = "user-1",
//            UserEmail = "user@test.com",
//            TotalAmount = 200m,
//            ConfirmedAt = DateTime.UtcNow,
//            Items = new List<OrderLineItem>()
//        }));

//        var saga = await _db.SagaStates.FirstAsync(s => s.OrderId == orderId);
//        saga.Status.Should().Be(SagaStatus.Started);

//        // Step 2: Saga orchestrator sends ReserveInventoryCommand
//        // (simulated by manually advancing state)
//        saga.Status = SagaStatus.ReservingInventory;
//        saga.UpdatedAt = DateTime.UtcNow;
//        await _db.SaveChangesAsync();

//        // Step 3: Inventory reserved → payment processing
//        var invReserved = new InventoryReservedConsumer(_db, _publishEndpoint,
//            NullLogger<InventoryReservedConsumer>.Instance);
//        await invReserved.Consume(CreateContext(
//            new InventoryReservedEvent { OrderId = orderId }));

//        saga = await _db.SagaStates.FirstAsync(s => s.OrderId == orderId);
//        saga.Status.Should().Be(SagaStatus.ProcessingPayment);

//        // Step 4: Payment succeeded → committing
//        var paySucceeded = new PaymentSucceededConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentSucceededConsumer>.Instance);
//        await paySucceeded.Consume(CreateContext(new PaymentSucceededEvent
//        {
//            OrderId = orderId,
//            PaymentId = Guid.NewGuid(),
//            Amount = 200m
//        }));

//        saga = await _db.SagaStates.FirstAsync(s => s.OrderId == orderId);
//        saga.Status.Should().Be(SagaStatus.Completing);

//        // Step 5: Verify commands published in correct order
//        Received.InOrder(() =>
//        {
//            _publishEndpoint.Publish(
//                Arg.Any<ProcessPaymentCommand>(),
//                Arg.Any<CancellationToken>());
//            _publishEndpoint.Publish(
//                Arg.Any<CommitInventoryCommand>(),
//                Arg.Any<CancellationToken>());
//        });
//    }

//    [Fact]
//    public async Task StateMachine_FullCompensationPath_AllTransitionsCorrect()
//    {
//        // Arrange
//        var orderId = Guid.NewGuid();

//        // Saga in ProcessingPayment
//        var saga = CreateSaga(orderId, SagaStatus.ProcessingPayment);
//        _db.SagaStates.Add(saga);
//        await _db.SaveChangesAsync();

//        // Step 1: Payment fails → releasing inventory
//        var payFailed = new PaymentFailedConsumer(_db, _publishEndpoint,
//            NullLogger<PaymentFailedConsumer>.Instance);
//        await payFailed.Consume(CreateContext(new PaymentFailedEvent
//        {
//            OrderId = orderId,
//            Reason = "Declined"
//        }));

//        saga = await _db.SagaStates.FirstAsync(s => s.OrderId == orderId);
//        saga.Status.Should().Be(SagaStatus.ReleasingInventory);

//        // Step 2: Inventory released → cancelled
//        var invReleased = new InventoryReleasedConsumer(_db,
//            NullLogger<InventoryReleasedConsumer>.Instance);
//        await invReleased.Consume(CreateContext(
//            new InventoryReleasedEvent { OrderId = orderId }));

//        saga = await _db.SagaStates.FirstAsync(s => s.OrderId == orderId);
//        saga.Status.Should().Be(SagaStatus.Cancelled);
//        saga.FailureReason.Should().Be("Declined");

//        // Verify compensation commands published
//        await _publishEndpoint.Received(1).Publish(
//            Arg.Is<ReleaseInventoryCommand>(c => c.OrderId == orderId),
//            Arg.Any<CancellationToken>());
//    }

//    // ── Helpers ─────────────────────────────────────────────────────────────

//    private static OrderSagaState CreateSaga(Guid orderId, SagaStatus status) =>
//        new()
//        {
//            Id = Guid.NewGuid(),
//            OrderId = orderId,
//            Status = status,
//            UserId = "user-1",
//            UserEmail = "user@test.com",
//            TotalAmount = 100m,
//            ItemsJson = JsonSerializer.Serialize(new List<OrderLineItem>()),
//            CreatedAt = DateTime.UtcNow,
//            UpdatedAt = DateTime.UtcNow
//        };

//    private static ConsumeContext<T> CreateContext<T>(T message) where T : class
//    {
//        var ctx = Substitute.For<ConsumeContext<T>>();
//        ctx.Message.Returns(message);
//        ctx.CancellationToken.Returns(CancellationToken.None);
//        return ctx;
//    }

//    private IServiceScopeFactory CreateScopeFactory(
//        IPublishEndpoint? publishEndpoint = null)
//    {
//        var pe = publishEndpoint ?? _publishEndpoint;
//        var scope = Substitute.For<IServiceScope>();
//        var factory = Substitute.For<IServiceScopeFactory>();
//        var provider = Substitute.For<IServiceProvider>();

//        provider.GetService(typeof(OrderDbContext)).Returns(_db);
//        provider.GetService(typeof(IPublishEndpoint)).Returns(pe);
//        scope.ServiceProvider.Returns(provider);
//        factory.CreateScope().Returns(scope);

//        return factory;
//    }
//    public void Dispose()
//    {
//        _db.Dispose();
//    }
//}

//// ═══════════════════════════════════════════════════════════════════════════
//// OUTBOX TESTS
//// ═══════════════════════════════════════════════════════════════════════════

//public class OutboxTests : IDisposable
//{
//    private readonly OrderDbContext _db;
//    public OutboxTests()
//    {
//        var options = new DbContextOptionsBuilder<OrderDbContext>()
//            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
//            .ConfigureWarnings(w =>
//                w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
//            .Options;

//        _db = new OrderDbContext(options);
//    }

//    [Fact]
//    public async Task Outbox_NewEntry_HasPendingStatus()
//    {
//        var entry = new OutboxEntry
//        {
//            Id = Guid.NewGuid(),
//            EventType = "OrderConfirmedEvent",
//            Payload = "{}",
//            CreatedAt = DateTime.UtcNow
//        };

//        _db.OutboxEntries.Add(entry);
//        await _db.SaveChangesAsync();

//        var saved = await _db.OutboxEntries.FindAsync(entry.Id);
//        saved!.DeliveredAt.Should().BeNull();
//        saved.RetryCount.Should().Be(0);
//    }

//    [Fact]
//    public async Task Outbox_PendingQuery_ReturnsOnlyUndelivered()
//    {
//        _db.OutboxEntries.AddRange(
//            new OutboxEntry
//            {
//                Id = Guid.NewGuid(),
//                EventType = "OrderConfirmedEvent",
//                Payload = "{}",
//                CreatedAt = DateTime.UtcNow,
//                DeliveredAt = null
//            },
//            new OutboxEntry
//            {
//                Id = Guid.NewGuid(),
//                EventType = "OrderConfirmedEvent",
//                Payload = "{}",
//                CreatedAt = DateTime.UtcNow,
//                DeliveredAt = DateTime.UtcNow // already delivered
//            }
//        );
//        await _db.SaveChangesAsync();

//        var pending = await _db.OutboxEntries
//            .Where(e => e.DeliveredAt == null).ToListAsync();

//        pending.Should().HaveCount(1);
//    }

//    [Fact]
//    public async Task Outbox_AfterDelivery_HasDeliveredAtSet()
//    {
//        var entry = new OutboxEntry
//        {
//            Id = Guid.NewGuid(),
//            EventType = "OrderConfirmedEvent",
//            Payload = "{}",
//            CreatedAt = DateTime.UtcNow
//        };
//        _db.OutboxEntries.Add(entry);
//        await _db.SaveChangesAsync();

//        // Simulate delivery
//        entry.DeliveredAt = DateTime.UtcNow;
//        await _db.SaveChangesAsync();

//        var pending = await _db.OutboxEntries
//            .Where(e => e.DeliveredAt == null).CountAsync();

//        pending.Should().Be(0);
//    }

//    [Fact]
//    public async Task Outbox_FailedDelivery_IncrementsRetryCount()
//    {
//        var entry = new OutboxEntry
//        {
//            Id = Guid.NewGuid(),
//            EventType = "OrderConfirmedEvent",
//            Payload = "{}",
//            CreatedAt = DateTime.UtcNow,
//            RetryCount = 0
//        };
//        _db.OutboxEntries.Add(entry);
//        await _db.SaveChangesAsync();

//        // Simulate 3 failed delivery attempts
//        for (int i = 0; i < 3; i++)
//        {
//            entry.RetryCount++;
//            await _db.SaveChangesAsync();
//        }

//        var saved = await _db.OutboxEntries.FindAsync(entry.Id);
//        saved!.RetryCount.Should().Be(3);
//        saved.DeliveredAt.Should().BeNull(); // still not delivered
//    }

//    [Fact]
//    public async Task Outbox_MultipleEntries_DeliveredInChronologicalOrder()
//    {
//        var now = DateTime.UtcNow;
//        _db.OutboxEntries.AddRange(
//            new OutboxEntry
//            {
//                Id = Guid.NewGuid(),
//                EventType = "OrderConfirmedEvent",
//                Payload = "{\"order\":\"third\"}",
//                CreatedAt = now.AddMinutes(2)
//            },
//            new OutboxEntry
//            {
//                Id = Guid.NewGuid(),
//                EventType = "OrderConfirmedEvent",
//                Payload = "{\"order\":\"first\"}",
//                CreatedAt = now
//            },
//            new OutboxEntry
//            {
//                Id = Guid.NewGuid(),
//                EventType = "OrderConfirmedEvent",
//                Payload = "{\"order\":\"second\"}",
//                CreatedAt = now.AddMinutes(1)
//            }
//        );
//        await _db.SaveChangesAsync();

//        var pending = await _db.OutboxEntries
//            .Where(e => e.DeliveredAt == null)
//            .OrderBy(e => e.CreatedAt)
//            .ToListAsync();

//        pending[0].Payload.Should().Contain("first");
//        pending[1].Payload.Should().Contain("second");
//        pending[2].Payload.Should().Contain("third");
//    }

//    public void Dispose()
//    {
//        _db.Dispose();
//    }
//}