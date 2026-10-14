using Npgsql;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// Tests in this folder run against a REAL PostgreSQL and/or RabbitMQ, because what they check
// (row locks, transactions, the MassTransit outbox, AMQP queue operations) cannot be faked.
// They are skipped unless the matching environment variable is set, so a plain `dotnet test`
// on a laptop without those servers stays green:
//
//   SHOPEASY_TEST_POSTGRES   server connection string WITHOUT a database name, e.g.
//                            "Host=localhost;Username=postgres;Password=secret"
//                            (the role needs CREATEDB; every test creates and drops its own database)
//   SHOPEASY_TEST_RABBITMQ   AMQP URI, e.g. "amqp://guest:guest@localhost:5672"
//
// The README ("Running the tests") shows how to start both with Docker.
public static class TestInfrastructure
{
    public const string PostgresVariable = "SHOPEASY_TEST_POSTGRES";
    public const string RabbitMqVariable = "SHOPEASY_TEST_RABBITMQ";

    public static string? Postgres => Environment.GetEnvironmentVariable(PostgresVariable);
    public static string? RabbitMq => Environment.GetEnvironmentVariable(RabbitMqVariable);
}

public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestInfrastructure.Postgres))
            Skip = $"Needs PostgreSQL: set {TestInfrastructure.PostgresVariable}.";
    }
}

public sealed class RequiresRabbitMqFactAttribute : FactAttribute
{
    public RequiresRabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestInfrastructure.RabbitMq))
            Skip = $"Needs RabbitMQ: set {TestInfrastructure.RabbitMqVariable}.";
    }
}

// A throw-away database on the test PostgreSQL server.
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnectionStringBuilder _server;
    private readonly string _name;

    public string ConnectionString { get; }

    private PostgresTestDatabase(NpgsqlConnectionStringBuilder server, string name)
    {
        _server = server;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString)
        {
            Database = name,
            // a pool per test database would keep connections open after the database is dropped
            Pooling = false
        }.ConnectionString;
    }

    public static async Task<PostgresTestDatabase> CreateAsync(string purpose)
    {
        var server = new NpgsqlConnectionStringBuilder(TestInfrastructure.Postgres!)
        {
            Database = "postgres",
            Pooling = false
        };
        var name = $"shopeasy_test_{purpose}_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new PostgresTestDatabase(server, name);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
