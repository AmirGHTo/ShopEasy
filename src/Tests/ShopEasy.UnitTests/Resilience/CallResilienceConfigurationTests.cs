using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ShopEasy.Gateway.Resilience;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// The settings for calls between services (Resilience:Inventory, Resilience:Gateway and
// Resilience:RetryBudget) are written down in appsettings.json, the Compose file with its
// .env.example variables, and the Kubernetes ConfigMap. The code's defaults are the truth; these
// tests fail when one of the copies says something else.
public class CallResilienceConfigurationTests
{
    private const string OrderDirectory = "src/Services/ShopEasy.OrderService";
    private const string GatewayDirectory = "src/Gateway/ShopEasy.Gateway";

    private static Dictionary<string, double> Inventory => ResilienceConfigurationTests.Defaults<InventoryCallOptions>();
    private static Dictionary<string, double> Gateway => ResilienceConfigurationTests.Defaults<GatewayResilienceOptions>();
    private static Dictionary<string, double> Budget => ResilienceConfigurationTests.Defaults<RetryBudgetOptions>();

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static Dictionary<string, double>? JsonSection(string directory, string section)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read(directory, "appsettings.json"));
        if (!document.RootElement.TryGetProperty("Resilience", out var resilience)
            || !resilience.TryGetProperty(section, out var values))
            return null;
        return values.EnumerateObject().ToDictionary(p => p.Name, p => Number(p.Value.GetRawText()));
    }

    // "Resilience__Inventory__TimeoutSeconds: <value>" lines of a file
    private static Dictionary<string, double> EnvironmentLines(string text, string group, string valuePattern) =>
        Regex.Matches(text, $@"^\s*Resilience__{group}__(\w+):\s*""{valuePattern}""\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));

    internal static string ServiceBlock(string compose, string service)
    {
        var match = Regex.Match(compose, $@"^  {service}:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the Compose file should define {service}");
        return match.Value;
    }

    [Fact]
    public void TheOrderServicesAppsettings_ListTheInventoryAndBudgetDefaults()
    {
        JsonSection(OrderDirectory, "Inventory").Should().BeEquivalentTo(Inventory);
        JsonSection(OrderDirectory, "RetryBudget").Should().BeEquivalentTo(Budget);
        JsonSection(OrderDirectory, "Gateway").Should().BeNull("the proxy settings belong to the gateway");
    }

    [Fact]
    public void TheGatewaysAppsettings_ListTheProxyAndBudgetDefaults()
    {
        JsonSection(GatewayDirectory, "Gateway").Should().BeEquivalentTo(Gateway);
        JsonSection(GatewayDirectory, "RetryBudget").Should().BeEquivalentTo(Budget);
        JsonSection(GatewayDirectory, "Inventory").Should().BeNull("the gateway does not call Inventory itself");
    }

    [Fact]
    public void TheRealAppsettings_AreReadByTheServices_AndAnOverrideBeatsThem()
    {
        var order = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryFiles.Root, "src", "Services", "ShopEasy.OrderService", "appsettings.json"))
            .Build();
        var gateway = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryFiles.Root, "src", "Gateway", "ShopEasy.Gateway", "appsettings.json"))
            .Build();

        // what a service sees with nothing overridden: the documented defaults, valid and unchanged
        var inventory = order.GetSection(InventoryCallOptions.SectionName).Get<InventoryCallOptions>()!;
        inventory.Validate();
        inventory.TimeoutSeconds.Should().Be(3);
        inventory.MaxRetries.Should().Be(2);
        RetryBudgetOptions.From(order).Ratio.Should().Be(0.2);
        var proxy = gateway.GetSection(GatewayResilienceOptions.SectionName).Get<GatewayResilienceOptions>()!;
        proxy.Validate();
        proxy.ActivityTimeoutSeconds.Should().Be(30);
        proxy.GetRetries.Should().Be(2);

        // an environment variable (Resilience__Inventory__TimeoutSeconds, here as the key the
        // environment provider turns it into) is added after the file and therefore wins
        var overridden = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryFiles.Root, "src", "Services", "ShopEasy.OrderService", "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Inventory:TimeoutSeconds"] = "7",
                ["Resilience:RetryBudget:Ratio"] = "0.5"
            }).Build();
        overridden.GetSection(InventoryCallOptions.SectionName).Get<InventoryCallOptions>()!.TimeoutSeconds.Should().Be(7);
        RetryBudgetOptions.From(overridden).Ratio.Should().Be(0.5);
    }

    [Fact]
    public void Compose_PassesEverySettingToTheServiceThatUsesIt_WithTheSameDefault()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        const string withDefault = @"\$\{[A-Z_]+:-([0-9.]+)\}";

        var order = ServiceBlock(compose, "order");
        EnvironmentLines(order, "Inventory", withDefault).Should().BeEquivalentTo(Inventory);
        order.Should().Contain("*resilience-retry-budget");

        var gateway = ServiceBlock(compose, "gateway");
        EnvironmentLines(gateway, "Gateway", withDefault).Should().BeEquivalentTo(Gateway);
        gateway.Should().Contain("*resilience-retry-budget");

        EnvironmentLines(compose, "RetryBudget", withDefault).Should().BeEquivalentTo(Budget);
        EnvironmentLines(compose, "Inventory", withDefault).Should().HaveCount(Inventory.Count, "only Order calls Inventory");
        EnvironmentLines(compose, "Gateway", withDefault).Should().HaveCount(Gateway.Count, "only the gateway proxies");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasEverySettingWithTheSameValue()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        EnvironmentLines(configMap, "Inventory", "([0-9.]+)").Should().BeEquivalentTo(Inventory);
        EnvironmentLines(configMap, "Gateway", "([0-9.]+)").Should().BeEquivalentTo(Gateway);
        EnvironmentLines(configMap, "RetryBudget", "([0-9.]+)").Should().BeEquivalentTo(Budget);
    }

    [Fact]
    public void TheEnvExample_ShowsEveryVariableWithItsDefault()
    {
        var fromCompose = Regex.Matches(RepositoryFiles.Read("docker-compose.yml"),
                @"Resilience__(?:Inventory|Gateway|RetryBudget)__\w+:\s*""\$\{(RESILIENCE_[A-Z_]+):-([0-9.]+)\}""")
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));
        var fromExample = Regex.Matches(RepositoryFiles.Read(".env.example"), @"^# (RESILIENCE_[A-Z_]+)=([0-9.]+)\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => Number(m.Groups[2].Value));

        fromCompose.Should().HaveCount(Inventory.Count + Gateway.Count + Budget.Count);
        fromExample.Should().ContainKeys(fromCompose.Keys);
        foreach (var (name, value) in fromCompose)
            fromExample[name].Should().Be(value, $"{name} is shown with the Compose default");
    }
}
