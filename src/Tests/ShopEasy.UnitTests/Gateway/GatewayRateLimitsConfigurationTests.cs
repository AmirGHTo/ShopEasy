using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.Gateway.Security;
using ShopEasy.UnitTests.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The limits are written down in appsettings.json, the Compose file with .env.example, the Kubernetes
// ConfigMap and the README; the code's defaults are the truth, and these tests fail when a copy differs.
public class GatewayRateLimitsConfigurationTests
{
    private static readonly GatewayRateLimitSettings Defaults = new();

    // setting, the Compose variable, the default in the code
    public static TheoryData<string, string, int> Limits => new()
    {
        { nameof(GatewayRateLimitSettings.AnonymousPerMinute), "RATE_LIMIT_ANONYMOUS_PER_MINUTE", Defaults.AnonymousPerMinute },
        { nameof(GatewayRateLimitSettings.AuthenticatedPerMinute), "RATE_LIMIT_AUTHENTICATED_PER_MINUTE", Defaults.AuthenticatedPerMinute },
        { nameof(GatewayRateLimitSettings.AdminPerMinute), "RATE_LIMIT_ADMIN_PER_MINUTE", Defaults.AdminPerMinute },
        { nameof(GatewayRateLimitSettings.OrdersPerMinute), "RATE_LIMIT_ORDERS_PER_MINUTE", Defaults.OrdersPerMinute },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public void TheGatewaysAppsettings_ListTheDefault(string setting, string _, int expected)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read("src/Gateway/ShopEasy.Gateway", "appsettings.json"));

        document.RootElement.GetProperty("RateLimiting").GetProperty(setting).GetInt32().Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void Compose_PassesTheLimitToTheGatewayOnly_WithTheSameDefault(string setting, string variable, int expected)
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var gateway = CallResilienceConfigurationTests.ServiceBlock(compose, "gateway");

        Regex.Match(gateway, $@"RateLimiting__{setting}:\s*""\$\{{{variable}:-(\d+)\}}""").Groups[1].Value.Should().Be(expected.ToString());
        Regex.Matches(compose, $"RateLimiting__{setting}:").Count.Should().Be(1, "only the gateway has these limits");
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void TheKubernetesConfigMap_HasTheSameValue(string setting, string _, int expected)
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        Regex.Match(configMap, $@"RateLimiting__{setting}:\s*""(\d+)""").Groups[1].Value.Should().Be(expected.ToString());
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void TheEnvExample_ShowsTheVariableWithItsDefault(string _, string variable, int expected)
    {
        RepositoryFiles.Read(".env.example").Should().Contain($"# {variable}={expected}\n");
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void TheReadme_ExplainsTheSetting_WithItsDefault(string setting, string _, int expected)
    {
        var readme = RepositoryFiles.Read("README.md");

        var row = readme.Split('\n').Single(line => line.StartsWith("| `RateLimiting__AnonymousPerMinute`", StringComparison.Ordinal));
        row.Should().Contain($"`RateLimiting__{setting}`");
        row.Should().Contain($"`{expected}`");
    }

    [Fact]
    public void TheReadmesTableOfPolicies_StatesTheDefaults()
    {
        var readme = RepositoryFiles.Read("README.md");

        readme.Should().Contain($"| {Defaults.AnonymousPerMinute}/min per client IP |");
        readme.Should().Contain($"| {Defaults.AuthenticatedPerMinute}/min per user (admin {Defaults.AdminPerMinute}) |");
        readme.Should().Contain($"| {Defaults.OrdersPerMinute}/min per user |");
    }
}
