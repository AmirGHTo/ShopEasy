using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// What PostgresConnection puts into a service's connection string (no server needed).
public class PostgresConnectionTests
{
    private const string Plain = "Host=db;Database=shop;Username=app;Password=secret";

    private static NpgsqlConnectionStringBuilder Parse(string connectionString) => new(connectionString);

    [Fact]
    public void ByDefault_EveryWaitIsBounded()
    {
        var result = Parse(PostgresConnection.Harden(Plain, new PostgresResilienceOptions()));

        result.CommandTimeout.Should().Be(30);
        result.Timeout.Should().Be(15);
        result.MaxPoolSize.Should().Be(20);
        result.TcpKeepAlive.Should().BeTrue();
        result.TcpKeepAliveTime.Should().Be(30);
        result.TcpKeepAliveInterval.Should().Be(10);
        result.KeepAlive.Should().Be(10, "idle pooled connections are checked every 10 s");
        result.Options.Should().Contain("-c lock_timeout=10000");
        result.Options.Should().Contain("-c idle_in_transaction_session_timeout=60000");
    }

    [Fact]
    public void TheRestOfTheConnectionString_IsKept()
    {
        var result = Parse(PostgresConnection.Harden(Plain, new PostgresResilienceOptions()));

        result.Host.Should().Be("db");
        result.Database.Should().Be("shop");
        result.Username.Should().Be("app");
        result.Password.Should().Be("secret");
    }

    [Theory]
    [InlineData("Command Timeout=120")]
    [InlineData("CommandTimeout=120")]
    public void ASettingWrittenInTheConnectionString_IsNotOverridden(string setting)
    {
        var result = Parse(PostgresConnection.Harden($"{Plain};{setting}", new PostgresResilienceOptions()));

        result.CommandTimeout.Should().Be(120);
        result.Timeout.Should().Be(15, "the settings that were not written still get their limit");
    }

    [Fact]
    public void AnExplicitPoolSize_AndExplicitServerOptions_AreNotOverridden()
    {
        var result = Parse(PostgresConnection.Harden(
            $"{Plain};Maximum Pool Size=5;Options=-c lock_timeout=3000",
            new PostgresResilienceOptions()));

        result.MaxPoolSize.Should().Be(5);
        result.Options.Should().Contain("lock_timeout=3000").And.NotContain("lock_timeout=10000");
        result.Options.Should().Contain("idle_in_transaction_session_timeout=60000",
            "only the server setting that was given stays; the other one is still added");
    }

    [Fact]
    public void ZeroSwitchesALimitOff()
    {
        var result = Parse(PostgresConnection.Harden(Plain, new PostgresResilienceOptions
        {
            LockTimeoutSeconds = 0,
            IdleInTransactionTimeoutSeconds = 0,
            CommandTimeoutSeconds = 0,
            ConnectTimeoutSeconds = 0,
            TcpKeepAliveSeconds = 0,
            IdleCheckSeconds = 0
        }));

        result.Options.Should().BeNullOrEmpty();
        result.Keys.Cast<string>().Should().NotContain(
            ["Command Timeout", "Timeout", "TCP Keepalive", "TCP Keepalive Time", "TCP Keepalive Interval", "Keepalive"]);
    }

    [Fact]
    public void ReadsItsSettingsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Plain,
            ["Resilience:Postgres:CommandTimeoutSeconds"] = "45",
            ["Resilience:Postgres:MaxPoolSize"] = "8",
            ["Resilience:Postgres:LockTimeoutSeconds"] = "2"
        }).Build();

        var result = Parse(PostgresConnection.FromConfiguration(configuration));

        result.CommandTimeout.Should().Be(45);
        result.MaxPoolSize.Should().Be(8);
        result.Options.Should().Contain("-c lock_timeout=2000");
        result.Timeout.Should().Be(15, "unconfigured limits keep their defaults");
    }

    [Fact]
    public void AMissingConnectionString_IsAClearStartupError()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => PostgresConnection.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DefaultConnection*not found*");
    }

    [Theory]
    [InlineData(nameof(PostgresResilienceOptions.CommandTimeoutSeconds), -1)]
    [InlineData(nameof(PostgresResilienceOptions.LockTimeoutSeconds), -5)]
    [InlineData(nameof(PostgresResilienceOptions.MaxPoolSize), 0)]
    public void ANonsensicalLimit_FailsAtStartupNotAtThe3amOutage(string property, int value)
    {
        var options = new PostgresResilienceOptions();
        typeof(PostgresResilienceOptions).GetProperty(property)!.SetValue(options, value);

        var act = () => PostgresConnection.Harden(Plain, options);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{property}*");
    }
}
