using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using ShopEasy.OrderService.Data;
using ShopEasy.UnitTests.Infrastructure;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

public class OrderMigrationsTests
{
    // A model change without its migration would only show up when the service starts against a
    // database that lacks the table. The migrations' snapshot is compared with the current model.
    [Fact]
    public void TheMigrationsDescribeTheCurrentModel()
    {
        using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshot.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        differences.Should().BeEmpty("every change to OrderDbContext needs a migration");
    }

    [RequiresPostgresFact]
    public async Task MigratingADatabase_CreatesTheMassTransitInboxAndOutboxTables()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderoutbox");
        await using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(database.ConnectionString).Options);

        await db.Database.MigrateAsync();

        var tables = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        tables.Should().Contain(["InboxState", "OutboxMessage", "OutboxState", "OutboxEntries"]);
    }
}
