using System.Net.Sockets;
using Npgsql;

namespace ShopEasy.Shared.Resilience;

public static class TransientFaults
{
    // True when running the same operation again later can reasonably succeed: a database that
    // is restarting or unreachable, a dropped connection, a timeout, a deadlock or serialization
    // failure that Postgres asks you to retry. False for everything that will fail the same way
    // again: a unique-key violation, a null argument, a business rule, a cancellation (shutdown).
    //
    // Exceptions arrive wrapped (EF puts the NpgsqlException inside a DbUpdateException, tasks
    // inside AggregateException), so the whole chain is looked at.
    public static bool IsTransient(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 10; depth++)
        {
            switch (exception)
            {
                // The server ended the session: restart, failover or administrator action (57P0x),
                // a connection failure (08xxx), or idle_in_transaction_session_timeout (25P03,
                // what releases the locks of a hung process). Npgsql does not flag all of these
                // as transient, but a fresh connection works.
                case PostgresException { SqlState: "25P03" }:
                case PostgresException postgres when postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                                                     || postgres.SqlState.StartsWith("57P0", StringComparison.Ordinal):
                    return true;

                case NpgsqlException { IsTransient: true }:
                case TimeoutException:
                case SocketException:
                case HttpRequestException:
                    return true;

                // a network read or write that failed ("Unable to read data from the transport
                // connection"); not its file-system relatives (FileNotFoundException and so on),
                // which a retry will not fix
                case IOException when exception.GetType() == typeof(IOException):
                    return true;

                case AggregateException aggregate when aggregate.InnerExceptions.Any(e => IsTransient(e)):
                    return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }
}
