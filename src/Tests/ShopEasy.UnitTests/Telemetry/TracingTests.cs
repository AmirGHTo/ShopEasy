using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ShopEasy.Shared.Telemetry;
using Xunit;

namespace ShopEasy.UnitTests.Telemetry;

// What every service says about its traces: where they go, how many are kept, and which release,
// environment and copy of the service wrote them.
public class TracingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    // ── the settings ──────────────────────────────────────────────────────────

    [Fact]
    public void WithoutSettings_TheSpansGoToTheCollector_AndEveryTraceIsKept()
    {
        var settings = TelemetrySettings.From(Config());

        settings.Endpoint.Should().Be("http://otel-collector:4317/");
        settings.SamplingRatio.Should().Be(1.0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyEndpoint_SwitchesTheExportOff(string endpoint)
        => TelemetrySettings.From(Config((TelemetrySettings.EndpointKey, endpoint))).Endpoint.Should().BeNull();

    [Fact]
    public void TheEndpointComesFromConfiguration()
        => TelemetrySettings.From(Config((TelemetrySettings.EndpointKey, "https://traces.example.com:4317")))
            .Endpoint.Should().Be("https://traces.example.com:4317/");

    [Theory]
    [InlineData("otel-collector:4317")]
    [InlineData("ftp://otel-collector")]
    [InlineData("not a url")]
    public void AnEndpointThatIsNotAnHttpAddress_StopsTheService(string endpoint)
    {
        var from = () => TelemetrySettings.From(Config((TelemetrySettings.EndpointKey, endpoint)));

        from.Should().Throw<InvalidOperationException>().WithMessage($"*{TelemetrySettings.EndpointKey}*'{endpoint}'*");
    }

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("0.1", 0.1)]
    [InlineData("0", 0.0)]
    [InlineData(" 0.25 ", 0.25)]
    [InlineData("1.0", 1.0)]
    [InlineData("", 1.0)]
    public void TheSamplingRatioComesFromConfiguration(string configured, double expected)
        => TelemetrySettings.From(Config((TelemetrySettings.SamplingRatioKey, configured))).SamplingRatio.Should().Be(expected);

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("many")]
    [InlineData("NaN")]
    [InlineData("0,1")] // a decimal comma is not a number here, whatever the culture of the machine
    public void ASamplingRatioThatMakesNoSense_StopsTheService(string ratio)
    {
        var from = () => TelemetrySettings.From(Config((TelemetrySettings.SamplingRatioKey, ratio)));

        from.Should().Throw<InvalidOperationException>().WithMessage($"*{TelemetrySettings.SamplingRatioKey}*'{ratio}'*");
    }

    // ── the sampler ───────────────────────────────────────────────────────────

    private static SamplingResult Decide(Sampler sampler, ActivityContext parent = default) =>
        sampler.ShouldSample(new SamplingParameters(
            parent, ActivityTraceId.CreateRandom(), "work", ActivityKind.Server));

    private static ActivityContext Parent(bool sampled) =>
        new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
            sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None, isRemote: true);

    [Fact]
    public void ARatioOfOne_KeepsEveryTrace()
        => Enumerable.Range(0, 200).All(_ => Decide(Tracing.CreateSampler(1.0)).Decision == SamplingDecision.RecordAndSample)
            .Should().BeTrue();

    [Fact]
    public void ARatioOfZero_KeepsNoTrace()
        => Enumerable.Range(0, 200).All(_ => Decide(Tracing.CreateSampler(0.0)).Decision == SamplingDecision.Drop)
            .Should().BeTrue();

    [Fact]
    public void ARatioBetween_KeepsAboutThatShare()
    {
        var sampler = Tracing.CreateSampler(0.25);

        var kept = Enumerable.Range(0, 4000).Count(_ => Decide(sampler).Decision == SamplingDecision.RecordAndSample);

        kept.Should().BeInRange(800, 1200, "about a quarter of 4000 traces");
    }

    [Fact]
    public void ATraceThatWasKeptUpstream_IsKeptHere_WhateverTheRatio()
        => Decide(Tracing.CreateSampler(0.0), Parent(sampled: true)).Decision
            .Should().Be(SamplingDecision.RecordAndSample, "one trace is never kept by half of the services");

    [Fact]
    public void ATraceThatWasDroppedUpstream_IsDroppedHere_WhateverTheRatio()
        => Decide(Tracing.CreateSampler(1.0), Parent(sampled: false)).Decision
            .Should().Be(SamplingDecision.Drop);

    // ── what a trace says about the service ───────────────────────────────────

    private static Dictionary<string, object> Attributes(Resource resource) =>
        resource.Attributes.ToDictionary(a => a.Key, a => a.Value);

    [Fact]
    public void TheResource_NamesTheServiceItsReleaseItsEnvironmentAndItsCopy()
    {
        var attributes = Attributes(Tracing.CreateResource("ShopEasy.OrderService", "3f2c9ab", "Production").Build());

        attributes["service.name"].Should().Be("ShopEasy.OrderService");
        attributes["service.version"].Should().Be("3f2c9ab");
        attributes["deployment.environment"].Should().Be("Production");
        attributes["service.instance.id"].Should().Be(Environment.MachineName);
        attributes.Should().ContainKey("telemetry.sdk.name");
    }

    // ── all of it, through the real setup ─────────────────────────────────────

    private static readonly ActivitySource Source = new("ShopEasy.Tests.Tracing");

    // A service as Program.cs sets it up, with the spans kept in memory instead of sent anywhere
    private static (ServiceProvider Provider, List<Activity> Spans) Service(params (string Key, string? Value)[] settings)
    {
        var spans = new List<Activity>();
        var services = new ServiceCollection();
        services.AddShopEasyTracing(
            Config([(TelemetrySettings.EndpointKey, ""), (ShopEasy.Shared.Logging.ShopEasyLogging.ServiceVersionKey, "abc1234"), .. settings]),
            "ShopEasy.TestService", "Staging",
            tracing => tracing.AddSource(Source.Name).AddInMemoryExporter(spans));
        return (services.BuildServiceProvider(), spans);
    }

    [Fact]
    public void ARecordedSpan_IsExported_WithTheResourceOfTheService()
    {
        var (provider, spans) = Service();
        using var _ = provider;
        var tracer = provider.GetRequiredService<TracerProvider>();

        using (Source.StartActivity("work")) { }
        tracer.ForceFlush();

        spans.Should().ContainSingle(s => s.DisplayName == "work");
        var attributes = Attributes(tracer.GetResource());
        attributes["service.name"].Should().Be("ShopEasy.TestService");
        attributes["service.version"].Should().Be("abc1234");
        attributes["deployment.environment"].Should().Be("Staging");
    }

    [Fact]
    public void WithARatioOfZero_NothingIsExported_ButTheSpanAndItsIdsStillExist()
    {
        var (provider, spans) = Service((TelemetrySettings.SamplingRatioKey, "0"));
        using var _ = provider;
        var tracer = provider.GetRequiredService<TracerProvider>();

        string traceId;
        using (var span = Source.StartActivity("work"))
        {
            // the log lines of a request that was not sampled still carry its trace id
            span.Should().NotBeNull();
            span!.Recorded.Should().BeFalse();
            traceId = Activity.Current!.TraceId.ToHexString();
        }
        tracer.ForceFlush();

        traceId.Should().HaveLength(32);
        spans.Should().BeEmpty();
    }

    [Fact]
    public void ASettingThatMakesNoSense_StopsTheServiceWhileItIsSetUp()
    {
        var setUp = () => new ServiceCollection().AddShopEasyTracing(
            Config((TelemetrySettings.SamplingRatioKey, "2")), "ShopEasy.TestService", "Production");

        setUp.Should().Throw<InvalidOperationException>();
    }
}
