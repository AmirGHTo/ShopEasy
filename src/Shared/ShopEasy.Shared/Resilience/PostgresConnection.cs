using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ShopEasy.Shared.Resilience;

// Limits on how long a service waits for PostgreSQL, and how much of it one process can take.
// Without them a stuck lock, a half-dead connection or a slow query holds a request (and a
// connection) for as long as it takes, and every other request queues up behind it until the
// service is unresponsive: a slow database turns into a dead service.
//
// Settings live under "Resilience:Postgres"; 0 switches a limit off.
public class PostgresResilienceOptions
{
    public const string SectionName = "Resilience:Postgres";

    // A SELECT ... FOR UPDATE (the stock, saga and payment row locks) gives up after this long
    // instead of waiting for a lock holder that may be stuck. Postgres reports 55P03, which
    // TransientFaults treats as retryable, so a consumer simply tries again.
    public int LockTimeoutSeconds { get; set; } = 10;

    // The server ends a session that sits inside an open transaction doing nothing. That is what
    // releases the row locks of a pod that crashed or hung mid-transaction.
    public int IdleInTransactionTimeoutSeconds { get; set; } = 60;

    // Client side: a command that runs longer is cancelled (Npgsql sends a cancel request, the
    // server stops the statement). Also applies to the migrations at start-up.
    public int CommandTimeoutSeconds { get; set; } = 30;

    // Waiting for a connection: opening a new one, or a free one from the pool.
    public int ConnectTimeoutSeconds { get; set; } = 15;

    // Connections one process may open. With several replicas the sum must stay below the
    // server's max_connections: replicas x MaxPoolSize <= max_connections - reserve. When the pool
    // is used up callers wait ConnectTimeoutSeconds and then fail, instead of the database
    // refusing everyone.
    public int MaxPoolSize { get; set; } = 20;

    // TCP keep-alive probes find out that the other end vanished (database failover, a dropped
    // network path) in seconds instead of the operating system's default of hours.
    public int TcpKeepAliveSeconds { get; set; } = 30;

    // A connection that sits idle in the pool is checked with a tiny round trip this often. When
    // PostgreSQL restarts or fails over it closes every connection, but the pool only finds out
    // when a request picks one up, and that request fails: after a restart each pooled connection
    // costs one error (a 500 on an API call). The check finds the dead ones in the background and
    // drops them, so the first request after the database is back gets a fresh connection.
    public int IdleCheckSeconds { get; set; } = 10;

    internal void Validate()
    {
        void NotNegative(string name, int value)
        {
            if (value < 0)
                throw new InvalidOperationException($"{SectionName}:{name} must be 0 (off) or positive, not {value}.");
        }

        NotNegative(nameof(LockTimeoutSeconds), LockTimeoutSeconds);
        NotNegative(nameof(IdleInTransactionTimeoutSeconds), IdleInTransactionTimeoutSeconds);
        NotNegative(nameof(CommandTimeoutSeconds), CommandTimeoutSeconds);
        NotNegative(nameof(ConnectTimeoutSeconds), ConnectTimeoutSeconds);
        NotNegative(nameof(TcpKeepAliveSeconds), TcpKeepAliveSeconds);
        NotNegative(nameof(IdleCheckSeconds), IdleCheckSeconds);
        if (MaxPoolSize < 1)
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxPoolSize)} must be at least 1, not {MaxPoolSize}.");
    }
}

public static class PostgresConnection
{
    // The connection string of a service, with the limits above applied. Anything already
    // written in the connection string itself wins, so an operator can override one value
    // for one service without touching the others.
    public static string FromConfiguration(IConfiguration configuration, string name = "DefaultConnection")
    {
        var connectionString = configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' not found.");

        var options = configuration.GetSection(PostgresResilienceOptions.SectionName)
            .Get<PostgresResilienceOptions>() ?? new PostgresResilienceOptions();

        return Harden(connectionString, options);
    }

    public static string Harden(string connectionString, PostgresResilienceOptions options)
    {
        options.Validate();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // Keys lists only what the connection string itself sets (ContainsKey is true for every
        // known keyword), under the canonical name even when written as a synonym
        // ("CommandTimeout" -> "Command Timeout").
        var written = builder.Keys.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Default(string keyword, Action set)
        {
            if (!written.Contains(keyword))
                set();
        }

        if (options.CommandTimeoutSeconds > 0)
            Default("Command Timeout", () => builder.CommandTimeout = options.CommandTimeoutSeconds);
        if (options.ConnectTimeoutSeconds > 0)
            Default("Timeout", () => builder.Timeout = options.ConnectTimeoutSeconds);

        Default("Maximum Pool Size", () => builder.MaxPoolSize = options.MaxPoolSize);

        if (options.TcpKeepAliveSeconds > 0)
        {
            Default("Tcp Keepalive", () => builder.TcpKeepAlive = true);
            Default("Tcp Keepalive Time", () => builder.TcpKeepAliveTime = options.TcpKeepAliveSeconds);
            Default("Tcp Keepalive Interval", () => builder.TcpKeepAliveInterval = Math.Max(1, options.TcpKeepAliveSeconds / 3));
        }

        // Npgsql calls the idle check "Keepalive" (not to be confused with the TCP one above)
        if (options.IdleCheckSeconds > 0)
            Default("Keepalive", () => builder.KeepAlive = options.IdleCheckSeconds);

        // Server-side limits travel as startup parameters ("-c name=value", milliseconds)
        var serverSettings = new List<string>();
        var existing = builder.Options ?? "";
        if (options.LockTimeoutSeconds > 0 && !existing.Contains("lock_timeout"))
            serverSettings.Add($"-c lock_timeout={options.LockTimeoutSeconds * 1000}");
        if (options.IdleInTransactionTimeoutSeconds > 0 && !existing.Contains("idle_in_transaction_session_timeout"))
            serverSettings.Add($"-c idle_in_transaction_session_timeout={options.IdleInTransactionTimeoutSeconds * 1000}");
        if (serverSettings.Count > 0)
            builder.Options = string.Join(' ', new[] { existing }.Concat(serverSettings).Where(s => s.Length > 0));

        return builder.ConnectionString;
    }
}
