using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.OrderService.Idempotency;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The Idempotency settings are written down in the Order service's appsettings.json, the Compose
// file (with .env.example) and the Kubernetes ConfigMap. The code's defaults are the truth; these
// tests fail when one of the copies says something else.
public class IdempotencyConfigurationTests
{
    private static readonly IdempotencyOptions Defaults = new();

    [Fact]
    public void TheOrderServicesAppsettings_ListTheDefaults()
    {
        using var document = JsonDocument.Parse(
            RepositoryFiles.Read("src", "Services", "ShopEasy.OrderService", "appsettings.json"));

        var section = document.RootElement.GetProperty("Idempotency");

        section.GetProperty("RequireKey").GetBoolean().Should().Be(Defaults.RequireKey);
        section.GetProperty("RetentionHours").GetInt32().Should().Be(Defaults.RetentionHours);
        section.EnumerateObject().Should().HaveCount(2, "every setting is listed, and nothing else");
    }

    [Fact]
    public void Compose_PassesBothSettingsToTheOrderServiceOnly_WithTheDefaults()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        var order = Regex.Match(compose, @"^  order:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline).Value;
        order.Should().Contain(
            $"Idempotency__RequireKey: \"${{ORDER_IDEMPOTENCY_REQUIRE_KEY:-{Defaults.RequireKey.ToString().ToLowerInvariant()}}}\"");
        order.Should().Contain(
            $"Idempotency__RetentionHours: \"${{ORDER_IDEMPOTENCY_RETENTION_HOURS:-{Defaults.RetentionHours}}}\"");
        Regex.Matches(compose, "Idempotency__").Should().HaveCount(2, "only the Order service uses them");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasBothSettingsWithTheDefaults()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        configMap.Should().Contain($"Idempotency__RequireKey: \"{Defaults.RequireKey.ToString().ToLowerInvariant()}\"");
        configMap.Should().Contain($"Idempotency__RetentionHours: \"{Defaults.RetentionHours}\"");
    }

    [Fact]
    public void TheEnvExample_ShowsBothVariablesWithTheDefaults()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().Contain($"# ORDER_IDEMPOTENCY_REQUIRE_KEY={Defaults.RequireKey.ToString().ToLowerInvariant()}");
        example.Should().Contain($"# ORDER_IDEMPOTENCY_RETENTION_HOURS={Defaults.RetentionHours}");
    }
}
