using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopEasy.Identity.Data;
using ShopEasy.InventoryService.Data;
using ShopEasy.OrderService.Data;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// Replicas starting together take turns at the migrations (MigrationLock). Needs a real
// PostgreSQL: the lock is one of its advisory locks. See InfrastructureFacts.cs.
public class MigrationLockPostgresTests
{
    private static Task Run(PostgresTestDatabase database, string name, Func<Task> work, TimeSpan? wait = null) =>
        MigrationLock.RunAsync(database.ConnectionString, name, work, wait: wait);

    [RequiresPostgresFact]
    public async Task SeveralInstancesStartingTogether_ApplyTheMigrationsAndSeedOnce_AndNoneFails()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockstart");
        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(database.ConnectionString).Options;
        var go = new TaskCompletionSource();

        // the Inventory service's start-up, as eight replicas would run it at the same moment
        var instances = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            await using var db = new InventoryDbContext(options);
            await Run(database, nameof(InventoryDbContext), async () =>
            {
                await db.Database.MigrateAsync();
                if (!await db.InventoryItems.AnyAsync())
                    InventoryDbSeeder.Seed(db);
            });
        })).ToList();
        go.SetResult();
        await Task.WhenAll(instances).WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = new InventoryDbContext(options);
        (await check.InventoryItems.CountAsync()).Should().Be(5, "the seed ran once");
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    // The migrations of the Order and Identity services (the biggest ones) under the lock, eight instances at once
    private static async Task EightInstancesMigrate<TContext>(string purpose, Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        await using var database = await PostgresTestDatabase.CreateAsync(purpose);
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(database.ConnectionString).Options;
        var go = new TaskCompletionSource();

        var instances = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            await using var db = create(options);
            await Run(database, typeof(TContext).Name, () => db.Database.MigrateAsync());
        })).ToList();
        go.SetResult();
        await Task.WhenAll(instances).WaitAsync(TimeSpan.FromSeconds(60));

        await using var check = create(options);
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [RequiresPostgresFact]
    public Task TheOrderServicesMigrations_AreSafeWhenEightInstancesStartTogether() =>
        EightInstancesMigrate<OrderDbContext>("lockorder", o => new OrderDbContext(o));

    [RequiresPostgresFact]
    public Task TheIdentityServicesMigrations_AreSafeWhenEightInstancesStartTogether() =>
        EightInstancesMigrate<ApplicationDbContext>("lockidentity", o => new ApplicationDbContext(o));

    [RequiresPostgresFact]
    public async Task AnInstance_WaitsUntilTheOneHoldingTheLockIsDone()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockwait");
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = Run(database, "wait", async () => { holding.SetResult(); await release.Task; });
        await holding.Task;

        var ran = false;
        var second = Run(database, "wait", () => { ran = true; return Task.CompletedTask; });
        await Task.Delay(500);
        ran.Should().BeFalse("the first instance still holds the lock");

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        ran.Should().BeTrue();
    }

    [RequiresPostgresFact]
    public async Task TheWaitIsBounded_AndTheLockIsFreeAgainAfterwards()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockbound");
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = Run(database, "bound", async () => { holding.SetResult(); await release.Task; });
        await holding.Task;

        var gaveUp = await FluentActions.Awaiting(() => Run(database, "bound", () => Task.CompletedTask, TimeSpan.FromSeconds(1)))
            .Should().ThrowAsync<PostgresException>();
        gaveUp.Which.SqlState.Should().Be("55P03", "lock_not_available: the wait ran out");

        release.SetResult();
        await first;
        await Run(database, "bound", () => Task.CompletedTask);
    }

    [RequiresPostgresFact]
    public async Task AFailingMigration_ReleasesTheLock_ForTheNextInstance()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockfail");

        await FluentActions.Awaiting(() => Run(database, "fail", () => throw new InvalidOperationException("the migration failed")))
            .Should().ThrowAsync<InvalidOperationException>();

        await Run(database, "fail", () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [RequiresPostgresFact]
    public async Task DatabasesWithDifferentNames_DoNotWaitForEachOther()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockother");
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = Run(database, "one", async () => { holding.SetResult(); await release.Task; });
        await holding.Task;

        await Run(database, "two", () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));

        release.SetResult();
        await first;
    }
}
