using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.Shared.Telemetry;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Telemetry;

// The tracing settings are written down in each service's appsettings.json, the Compose file with
// .env.example, the Kubernetes ConfigMap and the README, and the Collector they point at is
// configured in two files. The code's defaults are the truth; these tests fail when a copy says
// something else, or when the services and the Collector stop agreeing on where the spans go.
public class TelemetryConfigurationTests
{
    private static readonly string Endpoint = TelemetrySettings.DefaultEndpoint;
    private static readonly double Ratio = TelemetrySettings.DefaultSamplingRatio;

    public static TheoryData<string> Services => new()
    {
        "src/Identity/ShopEasy.Identity",
        "src/Gateway/ShopEasy.Gateway",
        "src/Services/ShopEasy.ProductService",
        "src/Services/ShopEasy.InventoryService",
        "src/Services/ShopEasy.PaymentService",
        "src/Services/ShopEasy.OrderService"
    };

    [Theory]
    [MemberData(nameof(Services))]
    public void EveryServicesAppsettings_ListTheDefaults(string directory)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read(directory, "appsettings.json"));
        var section = document.RootElement.GetProperty("Telemetry");

        section.GetProperty("Endpoint").GetString().Should().Be(Endpoint);
        section.GetProperty("SamplingRatio").GetDouble().Should().Be(Ratio);
        section.EnumerateObject().Should().HaveCount(2, "every setting is listed, and nothing else");
    }

    [Fact]
    public void Compose_PassesBothSettingsToAllSixServices_WithTheDefaults()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        Regex.Match(compose, @"Telemetry__Endpoint:\s*""\$\{TELEMETRY_ENDPOINT:-([^}]+)\}""").Groups[1].Value
            .Should().Be(Endpoint);
        double.Parse(Regex.Match(compose, @"Telemetry__SamplingRatio:\s*""\$\{TELEMETRY_SAMPLING_RATIO:-([^}]+)\}""").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(Ratio);
        Regex.Matches(compose, @"<<: \[\*logging, \*telemetry\b").Count.Should().Be(6, "the six application services");
    }

    [Fact]
    public void TheKubernetesConfigMap_SwitchesTheExportOff_UntilACollectorIsNamed()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        Regex.Match(configMap, @"^\s*Telemetry__Endpoint:\s*""([^""]*)""", RegexOptions.Multiline).Groups[1].Value
            .Should().BeEmpty("the manifests do not run a Collector, so there is nothing to send to");
        double.Parse(Regex.Match(configMap, @"^\s*Telemetry__SamplingRatio:\s*""([^""]+)""", RegexOptions.Multiline).Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(Ratio);
    }

    [Fact]
    public void TheEnvExample_ShowsEveryVariableWithItsDefault()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().Contain($"# TELEMETRY_SAMPLING_RATIO={Ratio:0.0}\n");
        example.Should().Contain($"# TELEMETRY_ENDPOINT={Endpoint}\n");
        example.Should().Contain("# OTEL_COLLECTOR_CONFIG=otel-collector.yml\n");
    }

    [Fact]
    public void TheReadme_ExplainsBothSettings_WithTheirDefaults()
    {
        var readme = RepositoryFiles.Read("README.md");
        var lines = readme.Split('\n');

        lines.Single(l => l.StartsWith("| `Telemetry__Endpoint`", StringComparison.Ordinal)).Should().Contain($"`{Endpoint}`");
        lines.Single(l => l.StartsWith("| `Telemetry__SamplingRatio`", StringComparison.Ordinal)).Should().Contain($"`{Ratio:0.0}`");
    }

    // ── the Collector ─────────────────────────────────────────────────────────

    [Fact]
    public void TheServicesDefaultEndpoint_IsTheCollectorServiceOfTheComposeFile_OnItsOtlpPort()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var uri = new Uri(Endpoint);

        Regex.IsMatch(compose, $@"^  {Regex.Escape(uri.Host)}:\n", RegexOptions.Multiline)
            .Should().BeTrue("the services' default endpoint names a service the Compose file defines");
        RepositoryFiles.Read("otel-collector.yml").Should().Contain($"endpoint: 0.0.0.0:{uri.Port}");
    }

    [Theory]
    [InlineData("otel-collector.yml")]
    [InlineData("otel-collector-tail-sampling.yml")]
    public void BothCollectorConfigs_ReceiveOtlp_AndExportToTheJaegerOfTheComposeFile(string file)
    {
        var config = RepositoryFiles.Read(file);

        config.Should().Contain("endpoint: 0.0.0.0:4317").And.Contain("endpoint: 0.0.0.0:4318");
        config.Should().Contain("endpoint: jaeger:4317");
        Regex.IsMatch(RepositoryFiles.Read("docker-compose.yml"), @"^  jaeger:\n", RegexOptions.Multiline).Should().BeTrue();
        config.Should().Contain("memory_limiter", "the Collector must refuse data before it runs out of memory");
    }

    [Fact]
    public void OnlyTheTailSamplingConfig_Samples_AndItKeepsErrorsAndSlowTraces()
    {
        RepositoryFiles.Read("otel-collector.yml").Should().NotContain("tail_sampling");

        var tail = RepositoryFiles.Read("otel-collector-tail-sampling.yml");
        tail.Should().Contain("processors: [memory_limiter, tail_sampling, batch]");
        tail.Should().Contain("type: status_code").And.Contain("status_codes: [ERROR]");
        tail.Should().Contain("type: latency").And.Contain("threshold_ms:");
        tail.Should().Contain("type: probabilistic");
    }

    [Fact]
    public void Compose_RunsTheCollector_WithBothConfigsMounted_AndTheOneYouChoose()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var collector = Regex.Match(compose, @"^  otel-collector:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline).Value;

        collector.Should().Contain("./otel-collector.yml:/etc/otelcol/otel-collector.yml:ro");
        collector.Should().Contain("./otel-collector-tail-sampling.yml:/etc/otelcol/otel-collector-tail-sampling.yml:ro");
        collector.Should().Contain("--config=/etc/otelcol/${OTEL_COLLECTOR_CONFIG:-otel-collector.yml}");
        collector.Should().Contain("\"4317:4317\"");
        // Jaeger no longer publishes the same host ports
        Regex.Matches(compose, "\"4317:4317\"").Count.Should().Be(1);
    }

    [Fact]
    public void TheCollectorImage_IsTheSameInComposeAndInTheCheckThatValidatesItsConfigs()
    {
        var composeImage = Regex.Match(RepositoryFiles.Read("docker-compose.yml"),
            @"image:\s*(otel/opentelemetry-collector-contrib:[\w.\-]+)").Groups[1].Value;
        var ciImage = Regex.Match(RepositoryFiles.Read(".github", "workflows", "ci.yml"),
            @"(otel/opentelemetry-collector-contrib:[\w.\-]+) validate").Groups[1].Value;

        composeImage.Should().NotBeEmpty().And.NotContain(":latest");
        ciImage.Should().Be(composeImage, "what CI validates must be what Compose runs");
    }
}
