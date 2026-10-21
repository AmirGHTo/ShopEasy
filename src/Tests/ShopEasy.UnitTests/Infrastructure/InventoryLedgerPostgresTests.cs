using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShopEasy.InventoryService.Data;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The ledger's unique index (one row per order, product and kind of entry) on a real PostgreSQL:
// the InMemory provider does not enforce indexes. See InfrastructureFacts.cs.
public class InventoryLedgerPostgresTests
{
    private const string Index = "IX_StockTransactions_OrderId_ProductId_Reason";
    private const string MigrationBeforeTheIndex = "20261014084238_AddMassTransitInboxOutbox";
    private static readonly Guid Product = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static InventoryDbContext Context(PostgresTestDatabase database) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(database.ConnectionString).Options);

    private static StockTransaction Row(Guid? orderId, string reason) => new()
    {
        Id = Guid.NewGuid(), ProductId = Product, QuantityChange = -1,
        Reason = reason, OrderId = orderId, CreatedAt = DateTime.UtcNow
    };

    private static async Task<PostgresException> RefusedWith(Func<Task> action)
    {
        var thrown = await action.Should().ThrowAsync<Exception>();
        return (thrown.Which as PostgresException ?? thrown.Which.InnerException as PostgresException)!;
    }

    [RequiresPostgresFact]
    public async Task ASecondRow_ForTheSameOrderProductAndKind_IsRefusedByTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ledgerdup");
        await using (var setup = Context(database)) await setup.Database.MigrateAsync();
        var order = Guid.NewGuid();

        await using (var first = Context(database))
        {
            first.StockTransactions.Add(Row(order, StockReasons.Committed(order)));
            await first.SaveChangesAsync();
        }

        await using var again = Context(database);
        again.StockTransactions.Add(Row(order, StockReasons.Committed(order)));
        var refused = await RefusedWith(() => again.SaveChangesAsync());

        refused.SqlState.Should().Be("23505");
        refused.ConstraintName.Should().Be(Index);
    }

    [RequiresPostgresFact]
    public async Task EveryKindOfEntry_AndEveryManualAdjustment_StillFitsTheIndex()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ledgerkinds");
        await using var db = Context(database);
        await db.Database.MigrateAsync();
        var order = Guid.NewGuid();

        // the four kinds of one order and product, and two manual adjustments with the same text
        db.StockTransactions.AddRange(
            Row(order, StockReasons.Reserved(order)), Row(order, StockReasons.Committed(order)),
            Row(order, StockReasons.Released(order)), Row(order, StockReasons.Restocked(order)),
            Row(null, "Recount"), Row(null, "Recount"));
        await db.SaveChangesAsync();

        (await db.StockTransactions.CountAsync()).Should().Be(6);
    }

    [RequiresPostgresFact]
    public async Task ADatabaseWithTheLedgerAsItIs_TakesTheIndexInTheMigration()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ledgerupgrade");
        await using var db = Context(database);
        await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheIndex);
        var order = Guid.NewGuid();
        db.StockTransactions.AddRange(Row(order, StockReasons.Reserved(order)), Row(order, StockReasons.Committed(order)));
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();

        (await db.StockTransactions.CountAsync()).Should().Be(2, "no row is touched");
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(m => m.EndsWith("AddStockLedgerUniqueIndex"));
    }

    [RequiresPostgresFact]
    public async Task ADatabaseThatAlreadyHoldsADuplicate_StopsTheMigration_NamingTheIndex()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ledgerolddup");
        await using var db = Context(database);
        await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheIndex);
        var order = Guid.NewGuid();
        db.StockTransactions.AddRange(Row(order, StockReasons.Committed(order)), Row(order, StockReasons.Committed(order)));
        await db.SaveChangesAsync();

        var refused = await RefusedWith(() => db.Database.MigrateAsync());

        refused.SqlState.Should().Be("23505");
        refused.MessageText.Should().Contain(Index);
    }
}
