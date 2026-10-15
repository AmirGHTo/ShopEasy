using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ShopEasy.UnitTests.Support;

// Makes the next database commands of a DbContext fail the way they do when PostgreSQL goes away
// (restart, failover, a dropped connection): an NpgsqlException caused by an IOException. Attach it
// with AddInterceptors, arm it AFTER the test's own set-up (migrations, seeding), and every
// command is refused until the given number of failures has been delivered. It also remembers the
// failures the server itself reported, so a test can prove that, say, a lock timeout really fired.
public sealed class FaultInjector : DbCommandInterceptor
{
    private int _remaining;
    private int _injected;

    public int Injected => Volatile.Read(ref _injected);

    // SQLSTATE of every error the server reported (for example "55P03" for a lock timeout)
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    public void FailNext(int commands) => Volatile.Write(ref _remaining, commands);

    private void ThrowIfArmed()
    {
        while (true)
        {
            var current = Volatile.Read(ref _remaining);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref _remaining, current - 1, current) != current) continue;

            Interlocked.Increment(ref _injected);
            throw new NpgsqlException(
                "injected: the database connection was lost", new IOException("connection reset by peer"));
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfArmed();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmed();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfArmed();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmed();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ThrowIfArmed();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmed();
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Record(eventData);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return Task.CompletedTask;
    }

    private void Record(CommandErrorEventData eventData)
    {
        if (eventData.Exception is PostgresException postgres)
            ServerErrors.Enqueue(postgres.SqlState);
    }
}
