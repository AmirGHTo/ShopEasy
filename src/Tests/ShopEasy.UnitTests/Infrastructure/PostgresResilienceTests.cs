using System.Diagnostics;
using FluentAssertions;
using Npgsql;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The limits PostgresConnection.Harden puts on a service's connections, against a real server:
// what each one does when the database is slow or stuck instead of down. See InfrastructureFacts.cs.
public class PostgresResilienceTests
{
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CreateStockTableAsync(PostgresTestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE stock (id int PRIMARY KEY, qty int); INSERT INTO stock VALUES (1, 10)");
        return database.ConnectionString;
    }

    private const string LockRow = "SELECT * FROM stock WHERE id = 1 FOR UPDATE";

    // ── a lock holder that never lets go ─────────────────────────────────────

    [RequiresPostgresFact]
    public async Task AWaiterBehindAStuckRowLock_GivesUpAfterTheLockTimeout()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("lockto");
        var plain = await CreateStockTableAsync(database);

        await using var holder = new NpgsqlConnection(plain);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await ExecuteAsync(holder, LockRow, holderTransaction); // ...and never commits

        var hardened = PostgresConnection.Harden(plain, new PostgresResilienceOptions
        {
            LockTimeoutSeconds = 1, CommandTimeoutSeconds = 30
        });
        await using var waiter = new NpgsqlConnection(hardened);
        await waiter.OpenAsync();
        await using var waiterTransaction = await waiter.BeginTransactionAsync();

        var clock = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => ExecuteAsync(waiter, LockRow, waiterTransaction));
        clock.Stop();

        failure.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        TransientFaults.IsTransient(failure).Should().BeTrue("the consumer should retry once the holder is gone");
    }

    [RequiresPostgresFact]
    public async Task WithoutTheLimits_TheSameWaiterJustHangs()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("nolimit");
        var plain = await CreateStockTableAsync(database);

        await using var holder = new NpgsqlConnection(plain);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await ExecuteAsync(holder, LockRow, holderTransaction);

        // only the client-side timeout is set, short, so that the test can stop the wait
        var unhardened = new NpgsqlConnectionStringBuilder(plain) { CommandTimeout = 3 }.ConnectionString;
        await using var waiter = new NpgsqlConnection(unhardened);
        await waiter.OpenAsync();
        await using var waiterTransaction = await waiter.BeginTransactionAsync();

        var clock = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => ExecuteAsync(waiter, LockRow, waiterTransaction));
        clock.Stop();

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5),
            "without a lock timeout the waiter stays queued behind the holder until something else gives up");
        failure.Should().NotBeNull();
    }

    // ── a pod that died or hung inside a transaction ─────────────────────────

    [RequiresPostgresFact]
    public async Task ASessionLeftIdleInsideATransaction_IsEndedByTheServer_AndItsLocksAreReleased()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idletx");
        var plain = await CreateStockTableAsync(database);

        var hardened = PostgresConnection.Harden(plain, new PostgresResilienceOptions { IdleInTransactionTimeoutSeconds = 1 });
        await using var stuck = new NpgsqlConnection(hardened);
        await stuck.OpenAsync();
        await using var stuckTransaction = await stuck.BeginTransactionAsync();
        await ExecuteAsync(stuck, LockRow, stuckTransaction);

        await Task.Delay(TimeSpan.FromSeconds(2.5)); // the process is hung: nothing is sent

        // everybody else can take the row now (they would have waited forever otherwise)
        await using var other = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(plain) { CommandTimeout = 10 }.ConnectionString);
        await other.OpenAsync();
        await using var otherTransaction = await other.BeginTransactionAsync();
        var clock = Stopwatch.StartNew();
        await ExecuteAsync(other, LockRow, otherTransaction);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // and if that process does wake up, it learns that its transaction is gone
        var failure = await Record.ExceptionAsync(() => ExecuteAsync(stuck, "SELECT 1", stuckTransaction));
        failure.Should().BeAssignableTo<NpgsqlException>();
        TransientFaults.IsTransient(failure).Should().BeTrue(
            $"the message is retried on a fresh connection; the failure was {failure}");
    }

    // A restart or failover ends every session with "terminating connection due to administrator
    // command"; the next command on such a connection fails and must count as retryable.
    [RequiresPostgresFact]
    public async Task ASessionTheServerTerminated_IsRetryable()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("killed");

        await using var victim = new NpgsqlConnection(database.ConnectionString);
        await victim.OpenAsync();
        int pid;
        await using (var ask = new NpgsqlCommand("SELECT pg_backend_pid()", victim))
            pid = (int)(await ask.ExecuteScalarAsync())!;

        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"SELECT pg_terminate_backend({pid})");
        }

        var failure = await Record.ExceptionAsync(() => ExecuteAsync(victim, "SELECT 1"));

        failure.Should().BeAssignableTo<NpgsqlException>();
        TransientFaults.IsTransient(failure).Should().BeTrue($"the failure was {failure}");
    }

    // ── a database that restarted ────────────────────────────────────────────

    // Terminating a backend is what the server does to every session when it restarts: the client gets
    // "57P01: terminating connection due to administrator command" the next time it uses the connection.
    private static async Task TerminateBackendAsync(string connectionString, int pid)
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"SELECT pg_terminate_backend({pid})");
    }

    // Uses a connection from the pool and gives it back, so it sits idle there afterwards
    private static async Task<int> BackendPidAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    // The test databases switch pooling off; the check is about connections that wait in a pool
    private static string Pooled(PostgresTestDatabase database, int idleCheckSeconds) =>
        new NpgsqlConnectionStringBuilder(PostgresConnection.Harden(database.ConnectionString,
            new PostgresResilienceOptions { IdleCheckSeconds = idleCheckSeconds })) { Pooling = true }.ConnectionString;

    [RequiresPostgresFact]
    public async Task AConnectionTheServerClosedWhileItWasIdle_IsDroppedByTheCheck_SoTheNextRequestWorks()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idlecheck");
        var pooled = Pooled(database, idleCheckSeconds: 1);
        try
        {
            var dead = await BackendPidAsync(pooled);
            await TerminateBackendAsync(database.ConnectionString, dead);
            await Task.Delay(TimeSpan.FromSeconds(5)); // the check runs every second and finds the dead connection

            var failure = await Record.ExceptionAsync(() => BackendPidAsync(pooled));

            failure.Should().BeNull("the pool already threw the dead connection away and opened a new one");
            (await BackendPidAsync(pooled)).Should().NotBe(dead);
        }
        finally
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(pooled));
        }
    }

    [RequiresPostgresFact]
    public async Task WithoutTheCheck_TheFirstRequestOnThatConnectionFails_AsEveryRestartUsedTo()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("noidlecheck");
        var pooled = Pooled(database, idleCheckSeconds: 0);
        try
        {
            var dead = await BackendPidAsync(pooled);
            await TerminateBackendAsync(database.ConnectionString, dead);
            await Task.Delay(TimeSpan.FromSeconds(5));

            var failure = await Record.ExceptionAsync(() => BackendPidAsync(pooled));

            failure.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("57P01");
            (await BackendPidAsync(pooled)).Should().NotBe(dead, "only that one request is lost: the broken connection is gone afterwards");
        }
        finally
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(pooled));
        }
    }

    // ── a query that runs for ever ───────────────────────────────────────────

    [RequiresPostgresFact]
    public async Task ASlowQuery_IsCancelledAtTheCommandTimeout_AndTheConnectionStaysUsable()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("cmdto");
        var hardened = PostgresConnection.Harden(database.ConnectionString, new PostgresResilienceOptions
        {
            CommandTimeoutSeconds = 1
        });
        await using var connection = new NpgsqlConnection(hardened);
        await connection.OpenAsync();

        var clock = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => ExecuteAsync(connection, "SELECT pg_sleep(30)"));
        clock.Stop();

        failure.Should().BeAssignableTo<NpgsqlException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "not the 30 seconds the query asked for");
        TransientFaults.IsTransient(failure).Should().BeTrue();

        // the server stopped the statement; the connection is not left running it
        await ExecuteAsync(connection, "SELECT 1");
    }

    // ── a process that wants more connections than it may have ───────────────

    [RequiresPostgresFact]
    public async Task WhenThePoolIsUsedUp_ACallerWaitsTheConnectTimeoutAndThenFails_InsteadOfHoggingTheServer()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("pool");
        var hardened = new NpgsqlConnectionStringBuilder(PostgresConnection.Harden(
            database.ConnectionString,
            new PostgresResilienceOptions { MaxPoolSize = 2, ConnectTimeoutSeconds = 1 }))
        {
            Pooling = true
        }.ConnectionString;

        var first = new NpgsqlConnection(hardened);
        var second = new NpgsqlConnection(hardened);
        try
        {
            await first.OpenAsync();
            await second.OpenAsync();

            var third = new NpgsqlConnection(hardened);
            var clock = Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(() => third.OpenAsync());
            clock.Stop();

            failure.Should().NotBeNull();
            clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.5)).And.BeLessThan(TimeSpan.FromSeconds(8));
            TransientFaults.IsTransient(failure).Should().BeTrue("a busy pool frees up; the consumer retries");

            // the server never saw more than the two allowed connections
            await using var counter = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(database.ConnectionString).ConnectionString);
            await counter.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", counter);
            ((long)(await command.ExecuteScalarAsync())!).Should().BeLessThanOrEqualTo(2);

            // once a connection is returned, work continues
            await first.CloseAsync();
            await using var fourth = new NpgsqlConnection(hardened);
            await fourth.OpenAsync();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            NpgsqlConnection.ClearPool(new NpgsqlConnection(hardened));
        }
    }
}
