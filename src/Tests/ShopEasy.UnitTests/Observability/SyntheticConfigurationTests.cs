using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.EndToEndTests.Support;
using ShopEasy.Probe;
using ShopEasy.Shared.Telemetry;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// The probe is set up in several files: Compose (the container, and the account that Order and Payment leave
// out of the business numbers), .env.example, Prometheus (where to find it), the rules (what to alert on)
// and the services' appsettings.json. The code is the truth; these tests fail when a copy says something else.
[Collection(MetricsCollection.Name)]
public class SyntheticConfigurationTests
{
    private static readonly string Compose = RepositoryFiles.Read("docker-compose.yml");

    private static string Service(string name) =>
        Regex.Match(Compose, $@"^  {name}:\n(.*?)(?=^  [a-z][a-z-]*:\n|^\S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Value;

    // ── the account that is left out of the business numbers ──────────────────

    [Theory]
    [InlineData("OrderService")]
    [InlineData("PaymentService")]
    public void TheServices_ListTheSettingInTheirAppsettings_EmptyByDefault(string service)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read("src", "Services", $"ShopEasy.{service}", "appsettings.json"));

        var section = document.RootElement.GetProperty("Synthetic");

        section.GetProperty("ProbeEmail").GetString().Should().BeEmpty("without a probe account every order counts");
        section.EnumerateObject().Should().HaveCount(1);
    }

    [Fact]
    public void Compose_PassesThePresetAddressToOrderAndPaymentOnly_AsTheSettingTheCodeReads()
    {
        var name = SyntheticTraffic.SettingName.Replace(":", "__");

        Compose.Should().Contain($"x-synthetic: &synthetic\n  {name}: \"${{PROBE_EMAIL:-}}\"");
        Regex.Matches(Compose, @"\*synthetic\b").Should().HaveCount(2);
        Service("order").Should().Contain("*synthetic");
        Service("payment").Should().Contain("*synthetic");
    }

    [Fact]
    public void TheProbeAndTheServices_GetTheAccountFromTheSameVariable()
        => Service("probe").Should().Contain("Probe__Email: \"${PROBE_EMAIL:-}\"");

    // ── the probe's container ─────────────────────────────────────────────────

    [Fact]
    public void TheProbe_RunsOnlyWithItsProfile_AndIsHardenedLikeTheServices()
    {
        var probe = Service("probe");

        probe.Should().Contain("profiles: [\"probe\"]", "a plain `docker compose up` (and the CI job) must not start it");
        probe.Should().Contain("<<: *hardened");
        probe.Should().NotContain("ports:", "its metrics are read by Prometheus on the Compose network only");
        probe.Should().Contain("dockerfile: src/Tools/ShopEasy.Probe/Dockerfile");
    }

    [Fact]
    public void TheProbe_DefaultsInCompose_AreTheDefaultsOfTheCode()
    {
        var defaults = new ProbeSettings();
        var probe = Service("probe");

        probe.Should().Contain($"Probe__IntervalSeconds: \"${{PROBE_INTERVAL_SECONDS:-{defaults.IntervalSeconds}}}\"");
        probe.Should().Contain($"Probe__StepTimeoutSeconds: \"${{PROBE_STEP_TIMEOUT_SECONDS:-{defaults.StepTimeoutSeconds}}}\"");
    }

    [Fact]
    public void TheProbe_ReachesTheGatewayAndIdentity_ByTheAddressesComposePublishesThem()
    {
        var probe = Service("probe");

        // the public addresses the shop's redirects name are the published ports ...
        Service("gateway").Should().Contain("\"5000:8080\"");
        Service("identity").Should().Contain("\"5001:8080\"");
        probe.Should().Contain($"E2E_GATEWAY_URL: \"{new Uri(Stack.Gateway, "/").ToString().TrimEnd('/')}\"",
            "the default of the end-to-end tests' Stack is the published gateway port");
        probe.Should().Contain("${IDENTITY_PUBLIC_URL:-http://localhost:5001}");
        // ... and the connection goes to the containers behind them
        probe.Should().Contain("PROBE_ADDRESS_MAP:-localhost:5000=gateway:8080,localhost:5001=identity:8080");
        Stack.ParseAddressMap("localhost:5000=gateway:8080,localhost:5001=identity:8080").Should().HaveCount(2);
    }

    [Fact]
    public void TheProbe_WaitsForTheGatewayAndIdentity()
    {
        var probe = Service("probe");

        probe.Should().MatchRegex(@"gateway:\s+condition: service_healthy");
        probe.Should().MatchRegex(@"identity:\s+condition: service_healthy");
    }

    [Fact]
    public void TheEnvExample_HasAnEmptyAccount_SoTheProbeIsOffUntilSomeoneSetsIt()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().MatchRegex(@"(?m)^PROBE_EMAIL=$");
        example.Should().MatchRegex(@"(?m)^PROBE_PASSWORD=$", "a password is never committed");
    }

    // ── Prometheus ────────────────────────────────────────────────────────────

    [Fact]
    public void Prometheus_FindsTheProbeInDns_OnItsMetricsPort_AndNotUnderTheNameOfAService()
    {
        var config = RepositoryFiles.Read("prometheus.yml");

        var job = Regex.Match(config, @"- job_name: 'probe'\n(.*?)(?=\n  - job_name:|\z)", RegexOptions.Singleline).Value;
        job.Should().Contain("dns_sd_configs").And.Contain("names: ['probe']").And.Contain("type: A");
        job.Should().Contain($"port: {new ProbeSettings().MetricsPort}");
        job.Should().NotContain("static_configs", "a stack without the probe has no target, where a static one would be down all the time");
        // ServiceDown watches up{job=~"shopeasy-.*"}: a stack without the probe must not raise it
        RepositoryFiles.Read("prometheus-rules.yml").Should().Contain("up{job=~\"shopeasy-.*\"}");
    }

    [Fact]
    public async Task EveryProbeMetric_TheRulesAreWrittenAgainst_IsOneTheProbeExports()
    {
        ProbeMetrics.Record(Journey.Steps.Select(step => new StepResult(step, true, 1)).ToList(), DateTimeOffset.UtcNow);
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var exposed = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        var named = Regex.Matches(RepositoryFiles.Read("prometheus-rules.yml"), @"shopeasy_probe_[a-z_]+")
            .Select(m => m.Value).Distinct().ToList();

        named.Should().NotBeEmpty();
        foreach (var metric in named)
            exposed.Should().Contain($"# TYPE {metric} ", $"an alert on {metric} would never fire if the probe does not export it");
    }
}
