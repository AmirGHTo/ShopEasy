using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The saga history (SagaTransitions) against a real PostgreSQL: the real consumers, the timeout
// service and the cancellation service move a saga through every path it can take, each on its
// own connection with the real row locking, and the rows left behind are what a person would
// read in the Saga Monitor. See InfrastructureFacts.cs.
public class SagaHistoryPostgresTests
{
    private const string Owner = "user-1";

    private static async Task<DbContextOptions<OrderDbContext>> CreateOrderDatabaseAsync(PostgresTestDatabase database)
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync();
        return options;
    }

    private static async Task<Guid> SeedAsync(
        DbContextOptions<OrderDbContext> options, SagaStatus status, DateTime? updatedAt = null, string? failureReason = null)
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = new OrderDbContext(options);
        db.Orders.Add(new Order
        {
            Id = orderId, UserId = Owner, UserEmail = "u@test.com",
            Status = OrderStatus.Pending, TotalAmount = 100m, CreatedAt = now, UpdatedAt = now
        });
        db.OrderSummaries.Add(new OrderSummary
        {
            OrderId = orderId, UserId = Owner, UserEmail = "u@test.com",
            StatusName = "Pending", StatusCode = (int)OrderStatus.Pending, TotalAmount = 100m,
            AmountDisplay = "$100.00", ItemCount = 1, ItemsSummary = "Widget x1",
            PlacedAt = now, UpdatedAt = now
        });
        db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = status,
            UserId = Owner, UserEmail = "u@test.com", TotalAmount = 100m, ItemsJson = "[]",
            FailureReason = failureReason, CorrelationId = "corr-" + orderId.ToString("N")[..6],
            CreatedAt = updatedAt ?? now, UpdatedAt = updatedAt ?? now
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private static async Task<List<SagaTransition>> HistoryAsync(DbContextOptions<OrderDbContext> options, Guid orderId)
    {
        await using var db = new OrderDbContext(options);
        return await db.SagaTransitions.AsNoTracking().Where(t => t.OrderId == orderId).OrderBy(t => t.Id).ToListAsync();
    }

    // The statuses in the order the saga entered them
    private static List<SagaStatus> Path(IEnumerable<SagaTransition> history) => history.Select(h => h.ToStatus).ToList();

    // Every row starts where the one before it ended, and the times only go forward
    private static void ShouldBeOneUnbrokenChain(List<SagaTransition> history)
    {
        history[0].FromStatus.Should().BeNull();
        for (var i = 1; i < history.Count; i++)
        {
            history[i].FromStatus.Should().Be(history[i - 1].ToStatus, $"row {i} starts where row {i - 1} ended");
            history[i].At.Should().BeOnOrAfter(history[i - 1].At);
            history[i].SecondsInPreviousStatus.Should().NotBeNull().And.Subject.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    // ── the consumers, as the saga's events reach them ────────────────────────

    private static OrderReadModelUpdater ReadModel(OrderDbContext db) =>
        new(db, NullLogger<OrderReadModelUpdater>.Instance);

    private static async Task ConsumeAsync<T>(
        DbContextOptions<OrderDbContext> options, T message,
        Func<OrderDbContext, IPublishEndpoint, IConsumer<T>> consumer) where T : class
    {
        await using var db = new OrderDbContext(options);
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        await consumer(db, Substitute.For<IPublishEndpoint>()).Consume(context);
    }

    private static readonly PostgresSagaLocking Locking = new();

    private static Task InventoryReservedAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new InventoryReservedEvent { OrderId = id },
            (db, p) => new InventoryReservedConsumer(db, Locking, p, NullLogger<InventoryReservedConsumer>.Instance));

    private static Task PaymentSucceededAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new PaymentSucceededEvent { OrderId = id, PaymentId = Guid.NewGuid(), Amount = 100m },
            (db, p) => new PaymentSucceededConsumer(db, Locking, p, ReadModel(db), NullLogger<PaymentSucceededConsumer>.Instance));

    private static Task PaymentFailedAsync(DbContextOptions<OrderDbContext> o, Guid id, string reason) =>
        ConsumeAsync(o, new PaymentFailedEvent { OrderId = id, Reason = reason },
            (db, p) => new PaymentFailedConsumer(db, Locking, p, ReadModel(db), NullLogger<PaymentFailedConsumer>.Instance));

    private static Task InventoryCommittedAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new InventoryCommittedEvent { OrderId = id },
            (db, _) => new InventoryCommittedConsumer(db, Locking, ReadModel(db), NullLogger<InventoryCommittedConsumer>.Instance));

    private static Task InventoryReleasedAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new InventoryReleasedEvent { OrderId = id },
            (db, _) => new InventoryReleasedConsumer(db, Locking, ReadModel(db), NullLogger<InventoryReleasedConsumer>.Instance));

    private static Task PaymentRefundedAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new PaymentRefundedEvent { OrderId = id, PaymentId = Guid.NewGuid(), Amount = 100m },
            (db, p) => new PaymentRefundedConsumer(db, Locking, p, ReadModel(db), NullLogger<PaymentRefundedConsumer>.Instance));

    private static Task InventoryRestockedAsync(DbContextOptions<OrderDbContext> o, Guid id) =>
        ConsumeAsync(o, new InventoryRestockedEvent { OrderId = id },
            (db, _) => new InventoryRestockedConsumer(db, Locking, ReadModel(db), NullLogger<InventoryRestockedConsumer>.Instance));

    private static async Task<CancelResult> CancelAsync(DbContextOptions<OrderDbContext> options, Guid orderId)
    {
        await using var db = new OrderDbContext(options);
        var service = new OrderCancellationService(
            db, Locking, Substitute.For<IPublishEndpoint>(), ReadModel(db), NullLogger<OrderCancellationService>.Instance);
        return await service.CancelAsync(orderId, Owner, isAdmin: false);
    }

    // ── every kind of path through the saga ───────────────────────────────────

    [RequiresPostgresFact]
    public async Task TheHappyPath_LeavesOneRowForEachStep_AndTheSagaKnowsWhenItEnteredTheLast()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_happy");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ReservingInventory);

        await InventoryReservedAsync(options, orderId);
        await PaymentSucceededAsync(options, orderId);
        await InventoryCommittedAsync(options, orderId);

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(
            SagaStatus.ReservingInventory, SagaStatus.ProcessingPayment, SagaStatus.Completing, SagaStatus.Completed);
        ShouldBeOneUnbrokenChain(history);
        history.Should().OnlyContain(h => h.Reason == null);
        history.Should().OnlyContain(h => h.CorrelationId != null && h.CorrelationId.StartsWith("corr-"));

        await using var check = new OrderDbContext(options);
        var saga = await check.SagaStates.AsNoTracking().SingleAsync(s => s.OrderId == orderId);
        saga.Status.Should().Be(SagaStatus.Completed);
        saga.StatusChangedAt.Should().Be(history[^1].At);
    }

    [RequiresPostgresFact]
    public async Task ADeclinedPayment_LeavesTheRowsOfTheCompensation_WithTheReason()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_decline");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment);

        await PaymentFailedAsync(options, orderId, "Card declined");
        await InventoryReleasedAsync(options, orderId);

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(SagaStatus.ProcessingPayment, SagaStatus.ReleasingInventory, SagaStatus.Cancelled);
        ShouldBeOneUnbrokenChain(history);
        history.Select(h => h.Reason).Should().Equal(null, "Card declined", "Card declined");
    }

    [RequiresPostgresFact]
    public async Task ATimeout_LeavesARow_WithTheTimeTheSagaWaited()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_timeout");
        var options = await CreateOrderDatabaseAsync(database);
        // the saga has been waiting for its payment for five minutes
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment, updatedAt: DateTime.UtcNow.AddMinutes(-5));
        await using (var seeded = new OrderDbContext(options))
        {
            var enteredAt = DateTime.UtcNow.AddMinutes(-5);
            await seeded.SagaStates.Where(s => s.OrderId == orderId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.StatusChangedAt, enteredAt));
        }

        await using (var db = new OrderDbContext(options))
        {
            var stale = await db.SagaStates.AsNoTracking().SingleAsync(s => s.OrderId == orderId);
            var timeout = new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance);
            await timeout.HandleTimedOutSaga(stale, db, Substitute.For<IPublishEndpoint>(), Locking, CancellationToken.None);
        }

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(SagaStatus.ProcessingPayment, SagaStatus.ReleasingInventory);
        history[1].Reason.Should().Be(FailureReasons.TimedOutWaitingForPayment);
        history[1].SecondsInPreviousStatus.Should().BeInRange(299, 330);
    }

    [RequiresPostgresFact]
    public async Task ACustomerCancel_LeavesTheRowsOfTheCompensation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_cancel");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment);

        (await CancelAsync(options, orderId)).Outcome.Should().Be(CancelOutcome.Cancelling);
        await InventoryReleasedAsync(options, orderId);

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(SagaStatus.ProcessingPayment, SagaStatus.ReleasingInventory, SagaStatus.Cancelled);
        ShouldBeOneUnbrokenChain(history);
        history.Select(h => h.Reason).Should().Equal(null, FailureReasons.CancelledByCustomer, FailureReasons.CancelledByCustomer);
    }

    [RequiresPostgresFact]
    public async Task ACancelBeforeAnythingWasReserved_IsOneRowAtATime()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_cancel_early");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.Started);

        (await CancelAsync(options, orderId)).Outcome.Should().Be(CancelOutcome.Cancelled);

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(SagaStatus.Started, SagaStatus.Cancelled);
        history[1].Reason.Should().Be(FailureReasons.CancelledByCustomer);
    }

    [RequiresPostgresFact]
    public async Task ACancelOfAPaidOrder_LeavesTheRefundAndRestockRows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_paid_cancel");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.Completed);

        (await CancelAsync(options, orderId)).Outcome.Should().Be(CancelOutcome.Cancelling);
        await PaymentRefundedAsync(options, orderId);
        await InventoryRestockedAsync(options, orderId);

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(
            SagaStatus.Completed, SagaStatus.RefundingPayment, SagaStatus.RestockingInventory, SagaStatus.Cancelled);
        ShouldBeOneUnbrokenChain(history);
        history.Skip(1).Should().OnlyContain(h => h.Reason == FailureReasons.CancelledByCustomer);
    }

    // ── written together with the change, or not at all ───────────────────────

    [RequiresPostgresFact]
    public async Task ARolledBackStatusChange_LeavesNoRow()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_rollback");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment);

        await using (var db = new OrderDbContext(options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var saga = await Locking.LockByOrderIdAsync(db, orderId);
            saga!.Status = SagaStatus.Completing;
            await db.SaveChangesAsync();
            (await db.SagaTransitions.CountAsync(t => t.OrderId == orderId)).Should().Be(2, "inside the transaction both are there");
            await transaction.RollbackAsync();
        }

        Path(await HistoryAsync(options, orderId)).Should().Equal(SagaStatus.ProcessingPayment);
        await using var check = new OrderDbContext(options);
        (await check.SagaStates.AsNoTracking().SingleAsync(s => s.OrderId == orderId)).Status
            .Should().Be(SagaStatus.ProcessingPayment);
    }

    [RequiresPostgresFact]
    public async Task ASaveThatFailsAndIsTriedAgainOnTheSameContext_RecordsTheChangeOnce()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_retry");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment);

        await using var db = new OrderDbContext(options);
        var saga = await db.SagaStates.SingleAsync(s => s.OrderId == orderId);
        saga.Status = SagaStatus.Completing;
        // a second saga for the same order: the unique index refuses it, and with it the whole save
        var duplicate = new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = SagaStatus.Started, UserId = Owner, UserEmail = "u@test.com",
            ItemsJson = "[]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.SagaStates.Add(duplicate);

        var firstTry = async () => await db.SaveChangesAsync();
        await firstTry.Should().ThrowAsync<DbUpdateException>();
        Path(await HistoryAsync(options, orderId)).Should().Equal(
            new[] { SagaStatus.ProcessingPayment }, "a failed save writes nothing");

        db.SagaStates.Remove(duplicate);
        await db.SaveChangesAsync();

        var history = await HistoryAsync(options, orderId);
        Path(history).Should().Equal(SagaStatus.ProcessingPayment, SagaStatus.Completing);
    }

    // ── clean-up ──────────────────────────────────────────────────────────────

    [RequiresPostgresFact]
    public async Task TheRetention_RemovesOldRowsOnly_InOneStatement()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_purge");
        var options = await CreateOrderDatabaseAsync(database);
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();

        await using (var db = new OrderDbContext(options))
        {
            db.SagaTransitions.AddRange(
                new SagaTransition { OrderId = orderId, SagaId = Guid.NewGuid(), ToStatus = SagaStatus.Started, At = now.AddDays(-45) },
                new SagaTransition { OrderId = orderId, SagaId = Guid.NewGuid(), ToStatus = SagaStatus.Completed, At = now.AddDays(-31) },
                new SagaTransition { OrderId = orderId, SagaId = Guid.NewGuid(), ToStatus = SagaStatus.Cancelled, At = now.AddDays(-29) },
                new SagaTransition { OrderId = orderId, SagaId = Guid.NewGuid(), ToStatus = SagaStatus.Failed, At = now });
            await db.SaveChangesAsync();
        }

        await using (var db = new OrderDbContext(options))
            (await SagaHistory.PurgeAsync(db, TimeSpan.FromDays(30), now)).Should().Be(2);

        Path(await HistoryAsync(options, orderId)).Should().Equal(SagaStatus.Cancelled, SagaStatus.Failed);
    }

    [RequiresPostgresFact]
    public async Task TheStatusesAreStoredAsNames_SoThatTheTableCanBeReadWithoutTheCode()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("history_names");
        var options = await CreateOrderDatabaseAsync(database);
        var orderId = await SeedAsync(options, SagaStatus.ProcessingPayment);
        await PaymentSucceededAsync(options, orderId);

        await using var db = new OrderDbContext(options);
        var names = await db.Database
            .SqlQuery<string>($"""SELECT "ToStatus" AS "Value" FROM "SagaTransitions" WHERE "OrderId" = {orderId} ORDER BY "Id" """)
            .ToListAsync();

        names.Should().Equal("ProcessingPayment", "Completing");
    }
}
