using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// The resilience settings are written down in several places so that an operator finds them where
// they look: each service's appsettings.json, the Compose file (with the .env.example variables
// that override them) and the Kubernetes ConfigMap. The code's own defaults are the truth; these
// tests fail when one of the copies says something else, so a documented value can never differ
// from what the service actually does.
public class ResilienceConfigurationTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopEasy.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("ShopEasy.sln not found above the test output.");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path]));

    // The code's defaults as "setting name -> value"
    private static Dictionary<string, double> Defaults<T>() where T : new()
    {
        var instance = new T();
        return typeof(T).GetProperties()
            .Where(p => p.PropertyType == typeof(int) || p.PropertyType == typeof(double))
            .ToDictionary(p => p.Name, p => Convert.ToDouble(p.GetValue(instance), CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, double> PostgresDefaults => Defaults<PostgresResilienceOptions>();
    private static Dictionary<string, double> ConsumerDefaults => Defaults<ConsumerResilienceOptions>();

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    public static TheoryData<string, bool> Services => new()
    {
        { "src/Identity/ShopEasy.Identity", false },
        { "src/Services/ShopEasy.ProductService", false },
        { "src/Services/ShopEasy.OrderService", true },
        { "src/Services/ShopEasy.InventoryService", true },
        { "src/Services/ShopEasy.PaymentService", true }
    };

    private static Dictionary<string, double>? JsonSection(string directory, string section)
    {
        using var document = JsonDocument.Parse(Read(directory, "appsettings.json"));
        if (!document.RootElement.TryGetProperty("Resilience", out var resilience)
            || !resilience.TryGetProperty(section, out var values))
            return null;
        return values.EnumerateObject().ToDictionary(p => p.Name, p => Number(p.Value.GetRawText()));
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void EveryServicesAppsettings_ListsTheDatabaseDefaults(string directory, bool _)
    {
        var section = JsonSection(directory, "Postgres");

        section.Should().NotBeNull($"{directory}/appsettings.json should show the Resilience:Postgres settings");
        section.Should().BeEquivalentTo(PostgresDefaults);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void TheMessageConsumingServicesAppsettings_ListTheConsumerDefaults(string directory, bool consumesMessages)
    {
        var section = JsonSection(directory, "Consumers");

        if (consumesMessages)
        {
            section.Should().NotBeNull($"{directory}/appsettings.json should show the Resilience:Consumers settings");
            section.Should().BeEquivalentTo(ConsumerDefaults);
        }
        else
        {
            section.Should().BeNull("a service without message consumers has nothing to tune here");
        }
    }

    [Fact]
    public void TheRealAppsettings_AreReadByTheServices_AndAnOverrideBeatsThem()
    {
        var file = Path.Combine(Root, "src", "Services", "ShopEasy.OrderService", "appsettings.json");
        var connection = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=db;Database=orders;Username=app;Password=secret"
        };

        // what a service sees with nothing overridden: appsettings.json, i.e. the documented defaults
        var plain = new ConfigurationBuilder().AddJsonFile(file).AddInMemoryCollection(connection).Build();
        var harden = new NpgsqlConnectionStringBuilder(PostgresConnection.FromConfiguration(plain));
        harden.MaxPoolSize.Should().Be(20);
        harden.CommandTimeout.Should().Be(30);
        ConsumerResilienceOptions.From(plain).TransientRetryLimit.Should().Be(12);

        // an environment variable (Resilience__Postgres__MaxPoolSize, here as the key the
        // environment provider turns it into) is added after the file and therefore wins
        var overridden = new ConfigurationBuilder().AddJsonFile(file).AddInMemoryCollection(connection)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Postgres:MaxPoolSize"] = "7",
                ["Resilience:Consumers:TransientRetryLimit"] = "3"
            }).Build();
        new NpgsqlConnectionStringBuilder(PostgresConnection.FromConfiguration(overridden)).MaxPoolSize.Should().Be(7);
        ConsumerResilienceOptions.From(overridden).TransientRetryLimit.Should().Be(3);
    }

    // "Resilience__Postgres__MaxPoolSize: <value>" lines of a file, value taken by the given pattern
    private static Dictionary<string, double> EnvironmentLines(string text, string group, string valuePattern) =>
        Regex.Matches(text, $@"^\s*Resilience__{group}__(\w+):\s*""{valuePattern}""\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));

    [Fact]
    public void Compose_PassesEverySettingWithTheSameDefault()
    {
        var compose = Read("docker-compose.yml");
        const string withDefault = @"\$\{[A-Z_]+:-([0-9.]+)\}";

        EnvironmentLines(compose, "Postgres", withDefault).Should().BeEquivalentTo(PostgresDefaults);
        EnvironmentLines(compose, "Consumers", withDefault).Should().BeEquivalentTo(ConsumerDefaults);
    }

    [Fact]
    public void TheKubernetesConfigMap_HasEverySettingWithTheSameValue()
    {
        var configMap = Read("k8s", "configmaps", "configmap.yaml");

        EnvironmentLines(configMap, "Postgres", "([0-9.]+)").Should().BeEquivalentTo(PostgresDefaults);
        EnvironmentLines(configMap, "Consumers", "([0-9.]+)").Should().BeEquivalentTo(ConsumerDefaults);
    }

    [Fact]
    public void TheEnvExample_ShowsEveryComposeVariableWithItsDefault()
    {
        var compose = Read("docker-compose.yml");
        var fromCompose = Regex.Matches(compose, @"Resilience__\w+:\s*""\$\{(RESILIENCE_[A-Z_]+):-([0-9.]+)\}""")
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));
        var fromExample = Regex.Matches(Read(".env.example"), @"^# (RESILIENCE_[A-Z_]+)=([0-9.]+)\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));

        fromCompose.Should().HaveCount(PostgresDefaults.Count + ConsumerDefaults.Count);
        fromExample.Should().BeEquivalentTo(fromCompose);
    }

    [Fact]
    public void EveryComposeServiceWithADatabase_GetsTheSettings()
    {
        var compose = Read("docker-compose.yml");

        foreach (var service in new[] { "identity", "product", "inventory", "payment", "order" })
            ServiceBlock(compose, service).Should().Contain("*resilience-postgres", $"{service} has a database");
        foreach (var service in new[] { "inventory", "payment", "order" })
            ServiceBlock(compose, service).Should().Contain("*resilience-consumers", $"{service} consumes messages");
    }

    private static string ServiceBlock(string compose, string service)
    {
        var match = Regex.Match(compose, $@"^  {service}:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the Compose file should define {service}");
        return match.Value;
    }
}
