using Microsoft.Extensions.Logging;
using Npgsql;

namespace ShopEasy.Shared.Resilience;

// Replicas of a service start at the same time, and each one applies the database migrations (and
// seeds a new database). EF Core 8 takes no lock for that: a replica that loses the race crashes on
// "relation already exists", or goes on while the winner is still creating tables, or seeds the
// same rows twice.
//
// With this lock the replicas take turns: the first applies the migrations, the others wait, then
// find nothing left to do. It is a PostgreSQL advisory lock held by a session of its own, so it
// disappears with that session: a replica that dies in the middle of a migration does not leave
// the others waiting. A replica gives up waiting after `wait` (the service then fails to start and
// is restarted by its orchestrator).
public static class MigrationLock
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);

    // `name` says which database this is (the name of its DbContext); every replica of a service
    // passes the same one, which is what makes them wait for each other.
    public static async Task RunAsync(
        string connectionString, string name, Func<Task> work,
        ILogger? logger = null, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var limit = wait ?? DefaultWait;
        if (limit < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(wait), "The wait for the migration lock must be at least a second.");
        var key = KeyFor(name);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // lock_timeout also bounds the wait for an advisory lock; the command timeout must not cut it short
        await Execute(connection, $"SET lock_timeout = {(int)limit.TotalMilliseconds}", limit, ct);

        if (!await TryLock(connection, key, ct))
        {
            logger?.LogInformation("Another instance is applying the database migrations: waiting for it");
            await Execute(connection, $"SELECT pg_advisory_lock({key})", limit, ct);
        }
        try
        {
            await work();
        }
        finally
        {
            // closing the connection would release it as well; this does not wait for that
            await Execute(connection, $"SELECT pg_advisory_unlock({key})", limit, CancellationToken.None);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, TimeSpan limit, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = (int)limit.TotalSeconds + 10 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TryLock(NpgsqlConnection connection, long key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({key})", connection);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    // One key per name (advisory locks are per database), the same in every replica: FNV-1a over the name
    private static long KeyFor(string name)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name))
            hash = (hash ^ b) * 1099511628211UL;
        return unchecked((long)hash);
    }
}
