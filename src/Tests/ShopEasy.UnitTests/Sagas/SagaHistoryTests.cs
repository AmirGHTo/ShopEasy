using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Data;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Sagas;

// Every change of a saga's status leaves a row in SagaTransitions, written by OrderDbContext itself
// in the same SaveChanges. These tests use the InMemory provider: what is checked is which rows
// appear, not what PostgreSQL does with them (Infrastructure/SagaHistoryPostgresTests does that).
[Collection(MetricsCollection.Name)]
public class SagaHistoryTests : IDisposable
{
    private readonly DbContextOptions<OrderDbContext> _options = new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private OrderDbContext NewContext() => new(_options);

    public void Dispose() => NewContext().Database.EnsureDeleted();

    private static OrderSagaState NewSaga(SagaStatus status, DateTime? updatedAt = null, string? correlationId = "corr-1")
    {
        var at = updatedAt ?? DateTime.UtcNow;
        return new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Status = status, UserId = "u1", UserEmail = "u1@test.com",
            TotalAmount = 10m, ItemsJson = "[]", CorrelationId = correlationId, CreatedAt = at, UpdatedAt = at
        };
    }

    private async Task<List<SagaTransition>> HistoryAsync(Guid orderId)
    {
        await using var db = NewContext();
        return await db.SagaTransitions.Where(t => t.OrderId == orderId).OrderBy(t => t.Id).ToListAsync();
    }

    // ── what leaves a row ─────────────────────────────────────────────────────

    [Fact]
    public async Task ANewSaga_StartsItsHistory_WithNoPreviousStatus()
    {
        var saga = NewSaga(SagaStatus.Started);
        await using (var db = NewContext())
        {
            db.SagaStates.Add(saga);
            await db.SaveChangesAsync();
        }

        var row = (await HistoryAsync(saga.OrderId)).Should().ContainSingle().Subject;
        row.FromStatus.Should().BeNull();
        row.ToStatus.Should().Be(SagaStatus.Started);
        row.SecondsInPreviousStatus.Should().BeNull();
        row.SagaId.Should().Be(saga.Id);
        row.CorrelationId.Should().Be("corr-1");
        row.At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AChangeOfStatus_AddsARow_WithTheStatusItLeftAndEntered()
    {
        var saga = NewSaga(SagaStatus.ReservingInventory);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.ProcessingPayment;
            await db.SaveChangesAsync();
        }

        var history = await HistoryAsync(saga.OrderId);
        history.Select(h => (h.FromStatus, h.ToStatus)).Should().Equal(
            ((SagaStatus?)null, SagaStatus.ReservingInventory),
            (SagaStatus.ReservingInventory, SagaStatus.ProcessingPayment));
        history[1].SecondsInPreviousStatus.Should().BeInRange(0, 5);
    }

    [Fact]
    public async Task ASaveThatDoesNotChangeTheStatus_AddsNothing()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.RetryCount++;
            loaded.UpdatedAt = DateTime.UtcNow;
            loaded.FailureReason = "still waiting";
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(saga.OrderId)).Should().HaveCount(1, "only the creation");
    }

    [Fact]
    public async Task AStatusSetToWhatItAlreadyWas_AddsNothing()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.ProcessingPayment;
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(saga.OrderId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task SeveralStatusesInOneSave_EachGetTheirOwnRow_OneForEachSaga()
    {
        var a = NewSaga(SagaStatus.ProcessingPayment);
        var b = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.AddRange(a, b); await db.SaveChangesAsync(); }

        await using (var db = NewContext())
        {
            foreach (var saga in await db.SagaStates.ToListAsync())
                saga.Status = SagaStatus.Completing;
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(a.OrderId)).Should().HaveCount(2);
        (await HistoryAsync(b.OrderId)).Should().HaveCount(2);
    }

    // ── what the row says ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheTimeInThePreviousStatus_IsMeasuredFromWhenTheSagaEnteredIt()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment);
        saga.StatusChangedAt = DateTime.UtcNow.AddSeconds(-90);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }
        // (the first save stamps StatusChangedAt itself: put the older value back, as if it had waited)
        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.StatusChangedAt = DateTime.UtcNow.AddSeconds(-90);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.Completing;
            await db.SaveChangesAsync();
        }

        var last = (await HistoryAsync(saga.OrderId)).Last();
        last.SecondsInPreviousStatus.Should().BeInRange(89, 95);
    }

    [Fact]
    public async Task ASagaFromBeforeTheHistory_UsesItsLastUpdateAsWhenItEnteredItsStatus()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment, updatedAt: DateTime.UtcNow.AddMinutes(-5));
        await using (var db = NewContext())
        {
            db.SagaStates.Add(saga);
            await db.SaveChangesAsync();
            // as if the row had been there before the column existed
            (await db.SagaStates.SingleAsync(s => s.Id == saga.Id)).StatusChangedAt = null;
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.ReleasingInventory;
            await db.SaveChangesAsync();
        }

        var last = (await HistoryAsync(saga.OrderId)).Last();
        last.SecondsInPreviousStatus.Should().BeInRange(299, 310);
    }

    [Fact]
    public async Task TheSagaRemembersWhenItEnteredItsStatus()
    {
        var saga = NewSaga(SagaStatus.Started);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        await using var check = NewContext();
        var stored = await check.SagaStates.SingleAsync(s => s.Id == saga.Id);
        var row = await check.SagaTransitions.SingleAsync(t => t.OrderId == saga.OrderId);
        stored.StatusChangedAt.Should().Be(row.At);
    }

    [Fact]
    public async Task TheFailureReasonOfTheSaga_IsOnTheRow()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.ReleasingInventory;
            loaded.FailureReason = "Card declined";
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(saga.OrderId)).Select(h => h.Reason).Should().Equal(
            null, "Card declined", "Card declined");
    }

    [Fact]
    public async Task ACancelReasonSavedBeforeTheStatusMoved_IsNotLost()
    {
        // a cancel that arrives while the stock commit is in flight records its reason first; the
        // refund that follows changes the status later
        var saga = NewSaga(SagaStatus.Completing);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }
        await using (var db = NewContext())
        {
            (await db.SagaStates.SingleAsync(s => s.Id == saga.Id)).FailureReason = "Cancelled by customer";
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext())
        {
            (await db.SagaStates.SingleAsync(s => s.Id == saga.Id)).Status = SagaStatus.RefundingPayment;
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(saga.OrderId)).Last().Reason.Should().Be("Cancelled by customer");
    }

    [Fact]
    public async Task WithoutACorrelationIdOnTheSaga_TheCurrentOneIsUsed()
    {
        var saga = NewSaga(SagaStatus.Started, correlationId: null);
        using (CorrelationContext.Push("from-the-request"))
        {
            await using var db = NewContext();
            db.SagaStates.Add(saga);
            await db.SaveChangesAsync();
        }

        (await HistoryAsync(saga.OrderId)).Single().CorrelationId.Should().Be("from-the-request");
    }

    [Fact]
    public async Task ALongReasonOrCorrelationId_IsCutToWhatTheColumnsHold()
    {
        var saga = NewSaga(SagaStatus.Cancelled, correlationId: new string('c', 400));
        saga.FailureReason = new string('r', 2000);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }

        var row = (await HistoryAsync(saga.OrderId)).Single();
        row.Reason.Should().HaveLength(SagaTransition.MaxReasonLength);
        row.CorrelationId.Should().HaveLength(SagaTransition.MaxCorrelationIdLength);
    }

    // ── the metric ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachStepIsMeasured_UnderTheStatusItLeftAndTheOneItEntered()
    {
        const string count = "shopeasy_saga_step_duration_seconds_count{from=\"ProcessingPayment\",to=\"Completing\"}";
        const string sum = "shopeasy_saga_step_duration_seconds_sum{from=\"ProcessingPayment\",to=\"Completing\"}";
        var countBefore = await MetricValues.ReadAsync(count);
        var sumBefore = await MetricValues.ReadAsync(sum);

        var saga = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }
        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.StatusChangedAt = DateTime.UtcNow.AddSeconds(-4);
            await db.SaveChangesAsync();
            loaded.Status = SagaStatus.Completing;
            await db.SaveChangesAsync();
        }

        (await MetricValues.ReadAsync(count)).Should().Be(countBefore + 1);
        (await MetricValues.ReadAsync(sum) - sumBefore).Should().BeInRange(3.9, 9);
    }

    [Fact]
    public async Task ACreatedSaga_IsNotAStep()
    {
        var before = await MetricValues.ReadAsync("shopeasy_saga_step_duration_seconds_count{from=\"Started\",to=\"Cancelled\"}");

        await using (var db = NewContext()) { db.SagaStates.Add(NewSaga(SagaStatus.Cancelled)); await db.SaveChangesAsync(); }

        (await MetricValues.ReadAsync("shopeasy_saga_step_duration_seconds_count{from=\"Started\",to=\"Cancelled\"}")).Should().Be(before);
    }

    // ── the timeline endpoint ─────────────────────────────────────────────────

    private OutboxController Controller(OrderDbContext db) =>
        new(db, null!, null!, NullLogger<OutboxController>.Instance);

    private static JsonElement Json(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task TheTimeline_ListsTheTransitionsInOrder_WithTheCurrentStatus()
    {
        var saga = NewSaga(SagaStatus.ProcessingPayment);
        await using (var db = NewContext()) { db.SagaStates.Add(saga); await db.SaveChangesAsync(); }
        await using (var db = NewContext())
        {
            var loaded = await db.SagaStates.SingleAsync(s => s.Id == saga.Id);
            loaded.Status = SagaStatus.ReleasingInventory;
            loaded.FailureReason = "Timed out waiting for payment";
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var json = Json(await Controller(read).GetSagaTimeline(saga.OrderId, CancellationToken.None));

        json.GetProperty("orderId").GetGuid().Should().Be(saga.OrderId);
        json.GetProperty("status").GetString().Should().Be("ReleasingInventory");
        var transitions = json.GetProperty("transitions").EnumerateArray().ToList();
        transitions.Select(t => (t.GetProperty("from").GetString(), t.GetProperty("to").GetString())).Should().Equal(
            ((string?)null, "ProcessingPayment"), ("ProcessingPayment", "ReleasingInventory"));
        transitions[1].GetProperty("reason").GetString().Should().Be("Timed out waiting for payment");
        json.GetProperty("totalSeconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task TheTimeline_OfASagaOlderThanTheHistory_IsEmptyNotMissing()
    {
        var saga = NewSaga(SagaStatus.Completed);
        await using (var db = NewContext())
        {
            db.SagaStates.Add(saga);
            await db.SaveChangesAsync();
            db.SagaTransitions.RemoveRange(db.SagaTransitions);
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var json = Json(await Controller(read).GetSagaTimeline(saga.OrderId, CancellationToken.None));

        json.GetProperty("status").GetString().Should().Be("Completed");
        json.GetProperty("transitions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TheTimeline_OfAnOrderWithoutASaga_IsNotFound()
    {
        await using var read = NewContext();

        (await Controller(read).GetSagaTimeline(Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TheTimeline_OfOneOrder_DoesNotShowAnothers()
    {
        var mine = NewSaga(SagaStatus.Started);
        var other = NewSaga(SagaStatus.Started);
        await using (var db = NewContext()) { db.SagaStates.AddRange(mine, other); await db.SaveChangesAsync(); }

        await using var read = NewContext();
        var json = Json(await Controller(read).GetSagaTimeline(mine.OrderId, CancellationToken.None));

        json.GetProperty("transitions").GetArrayLength().Should().Be(1);
    }

    // ── no way around it ──────────────────────────────────────────────────────

    [Fact]
    public void BothSaveChangesOverloads_AreTheContextsOwn()
    {
        // SaveChanges() and SaveChangesAsync() end in these two; if one were not overridden, a save
        // through it would skip the history
        foreach (var (name, parameters) in new (string, Type[])[]
                 {
                     (nameof(DbContext.SaveChanges), [typeof(bool)]),
                     (nameof(DbContext.SaveChangesAsync), [typeof(bool), typeof(CancellationToken)])
                 })
        {
            typeof(OrderDbContext).GetMethod(name, BindingFlags.Public | BindingFlags.Instance, parameters)!
                .DeclaringType.Should().Be(typeof(OrderDbContext), $"{name} must record the saga history");
        }
    }

    // Code that changes rows without SaveChanges: ExecuteUpdate and raw SQL
    private static bool ChangesSagasBehindTheTracker(string text) =>
        Regex.IsMatch(text, @"SagaStates[\s\S]{0,300}?\.ExecuteUpdate")
        || Regex.IsMatch(text, @"Execute(Sql|SqlRaw|SqlInterpolated)(Async)?\([\s\S]{0,200}?SagaStates", RegexOptions.IgnoreCase)
        || Regex.IsMatch(text, @"UPDATE\s+""?SagaStates", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("await db.SagaStates.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SagaStatus.Cancelled));")]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE \\\"SagaStates\\\" SET \\\"Status\\\" = 6\");")]
    [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"update SagaStates set status = 6 where id = {id}\");")]
    public void Control_TheCheckBelowDoesCatchAStatusChangedBehindTheTracker(string code)
        => ChangesSagasBehindTheTracker(code).Should().BeTrue();

    [Fact]
    public void NothingChangesASagaStatus_WithoutGoingThroughTheChangeTracker()
    {
        // ExecuteUpdate and raw SQL change rows without SaveChanges, hence without a history row
        var service = Path.Combine(RepositoryFiles.Root, "src", "Services", "ShopEasy.OrderService");
        var offenders = Directory.GetFiles(service, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => ChangesSagasBehindTheTracker(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("a status changed by a set-based update would leave no row in SagaTransitions");
    }
}
