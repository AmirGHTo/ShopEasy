using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.Gateway.Security;
using ShopEasy.UnitTests.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The settings are written down in appsettings.json, the Compose file with .env.example, the
// Kubernetes ConfigMap and the README; the code's defaults are the truth, and these tests fail when
// a copy differs.
public class SecurityHeadersConfigurationTests
{
    private static readonly SecurityHeadersSettings Defaults = new();

    [Fact]
    public void TheGatewaysAppsettings_ListTheDefaults()
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read("src/Gateway/ShopEasy.Gateway", "appsettings.json"));
        var section = document.RootElement.GetProperty("SecurityHeaders");

        section.GetProperty("Enabled").GetBoolean().Should().Be(Defaults.Enabled);
        section.GetProperty("ImageSources").GetString().Should().Be(Defaults.ImageSources);
    }

    [Fact]
    public void Compose_PassesTheSettingsToTheGatewayOnly_WithTheSameDefaults()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var gateway = CallResilienceConfigurationTests.ServiceBlock(compose, "gateway");

        Regex.Match(gateway, @"SecurityHeaders__Enabled:\s*""\$\{SECURITY_HEADERS_ENABLED:-(\w+)\}""").Groups[1].Value
            .Should().Be(Defaults.Enabled.ToString().ToLowerInvariant());
        Regex.Match(gateway, @"SecurityHeaders__ImageSources:\s*""\$\{SECURITY_HEADERS_IMAGE_SOURCES:-([^}]*)\}""").Groups[1].Value
            .Should().Be(Defaults.ImageSources);
        Regex.Matches(compose, "SecurityHeaders__Enabled").Count.Should().Be(1, "only the gateway sends these headers");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasTheSameValues()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        Regex.Match(configMap, @"SecurityHeaders__Enabled:\s*""(\w+)""").Groups[1].Value
            .Should().Be(Defaults.Enabled.ToString().ToLowerInvariant());
        Regex.Match(configMap, @"SecurityHeaders__ImageSources:\s*""([^""]*)""").Groups[1].Value.Should().Be(Defaults.ImageSources);
    }

    [Fact]
    public void TheEnvExample_ShowsBothVariablesWithTheirDefaults()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().Contain($"# SECURITY_HEADERS_IMAGE_SOURCES={Defaults.ImageSources}\n");
        example.Should().Contain($"# SECURITY_HEADERS_ENABLED={Defaults.Enabled.ToString().ToLowerInvariant()}\n");
    }

    [Fact]
    public void TheReadme_ExplainsTheSettings()
    {
        var readme = RepositoryFiles.Read("README.md");

        readme.Should().Contain("`SecurityHeaders__Enabled`").And.Contain("`SecurityHeaders__ImageSources`");
        readme.Should().Contain("**Security headers (gateway).**");
    }
}
