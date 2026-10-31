using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ShopEasy.Shared.Logging;
using ShopEasy.Shared.Messaging;
using ShopEasy.Shared.Middleware;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// A line in the logs and a trace in Jaeger must lead to each other: every line carries the TraceId
// and SpanId of the span that was active when it was written. These tests use the logger the
// services use (ApplyShopEasyDefaults), a real HTTP request through the correlation middleware, a
// real message through MassTransit's test harness, and the formatter the console uses in JSON mode.
public class TraceLoggingTests
{
    private sealed class Collector : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private static (Serilog.ILogger Logger, Collector Sink) Create()
    {
        var sink = new Collector();
        var logger = new LoggerConfiguration()
            .ApplyShopEasyDefaults("ShopEasy.OrderService", "Production", "3f2c9ab")
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static string Property(LogEvent logEvent, string name) =>
        ((ScalarValue)logEvent.Properties[name]).Value!.ToString()!;

    // Hands out spans for the named sources, as OpenTelemetry's AddSource does in the services
    private sealed class Spans : IDisposable
    {
        private readonly ActivityListener _listener;

        public Spans(params string[] sources)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => sources.Contains(source.Name),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static readonly ActivitySource Source = new("ShopEasy.Tests.Logging");

    // ── the enricher ──────────────────────────────────────────────────────────

    [Fact]
    public void InsideASpan_TheLineCarriesItsTraceIdAndSpanId()
    {
        using var spans = new Spans(Source.Name);
        var (logger, sink) = Create();

        using var span = Source.StartActivity("work")!;
        logger.Information("inside");

        var line = sink.Events.Single();
        Property(line, "TraceId").Should().Be(span.TraceId.ToHexString()).And.HaveLength(32);
        Property(line, "SpanId").Should().Be(span.SpanId.ToHexString()).And.HaveLength(16);
    }

    [Fact]
    public void ANestedSpanKeepsTheTraceAndHasItsOwnSpanId()
    {
        using var spans = new Spans(Source.Name);
        var (logger, sink) = Create();

        using var outer = Source.StartActivity("outer")!;
        logger.Information("outer line");
        using (var inner = Source.StartActivity("inner")!)
            logger.Information("inner line");
        logger.Information("outer again");

        var lines = sink.Events.ToArray();
        lines.Select(l => Property(l, "TraceId")).Distinct().Should().Equal(outer.TraceId.ToHexString());
        Property(lines[1], "SpanId").Should().NotBe(Property(lines[0], "SpanId"));
        Property(lines[2], "SpanId").Should().Be(Property(lines[0], "SpanId"));
    }

    [Fact]
    public void OutsideASpan_ThereAreNoIds_AndNoneLeftOverFromAnEarlierSpan()
    {
        using var spans = new Spans(Source.Name);
        var (logger, sink) = Create();

        using (Source.StartActivity("work"))
            logger.Information("during");
        logger.Information("after");

        var after = sink.Events.Last();
        after.Properties.Should().NotContainKeys("TraceId", "SpanId");
    }

    [Fact]
    public void TheTextLineShowsTheTraceId()
    {
        using var spans = new Spans(Source.Name);
        var (logger, sink) = Create();

        using var span = Source.StartActivity("work")!;
        using (CorrelationContext.Push("a1b2c3d4"))
            logger.Information("Order saved");

        var formatter = new Serilog.Formatting.Display.MessageTemplateTextFormatter(
            ShopEasyLogging.ConsoleTemplate.Replace("{Timestamp:HH:mm:ss} ", ""));
        using var writer = new StringWriter();
        formatter.Format(sink.Events.Single(), writer);

        writer.ToString().TrimEnd().Should().Be(
            $"[INF] [ShopEasy.OrderService] [a1b2c3d4] [{span.TraceId.ToHexString()}] Order saved");
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    private static JsonElement Json(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        ShopEasyLogging.JsonFormatter.Format(logEvent, writer);
        var text = writer.ToString().TrimEnd();

        text.Should().NotContain("\n", "one JSON object per line");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TheJsonLineIsOneValidObject_WithEveryPropertyAtTheTopLevel()
    {
        using var spans = new Spans(Source.Name);
        var (logger, sink) = Create();

        using var span = Source.StartActivity("work")!;
        using (CorrelationContext.Push("a1b2c3d4"))
            logger.Warning("Payment {PaymentId} declined", 42);

        var json = Json(sink.Events.Single());

        json.GetProperty("@m").GetString().Should().Be("Payment 42 declined");
        json.GetProperty("@l").GetString().Should().Be("Warning");
        json.GetProperty("@t").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("PaymentId").GetInt32().Should().Be(42);
        json.GetProperty("Service").GetString().Should().Be("ShopEasy.OrderService");
        json.GetProperty("ServiceVersion").GetString().Should().Be("3f2c9ab");
        json.GetProperty("Environment").GetString().Should().Be("Production");
        json.GetProperty("CorrelationId").GetString().Should().Be("a1b2c3d4");
        json.GetProperty("TraceId").GetString().Should().Be(span.TraceId.ToHexString());
        json.GetProperty("SpanId").GetString().Should().Be(span.SpanId.ToHexString());
    }

    [Fact]
    public void AnExceptionIsPartOfTheSameJsonLine()
    {
        var (logger, sink) = Create();

        logger.Error(new InvalidOperationException("boom"), "Failed to save the order");

        var json = Json(sink.Events.Single());
        json.GetProperty("@x").GetString().Should().Contain("InvalidOperationException").And.Contain("boom");
        json.GetProperty("@l").GetString().Should().Be("Error");
    }

    // ── a real HTTP request ───────────────────────────────────────────────────

    [Fact]
    public async Task ALogLineInsideARequest_HasTheTraceOfTheRequest_AndTheCallersTraceIsContinued()
    {
        using var spans = new Spans("Microsoft.AspNetCore");
        var (logger, sink) = Create();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        await using var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.MapGet("/work", () =>
        {
            logger.Information("handling the request");
            return Activity.Current!.TraceId.ToHexString();
        });
        await app.StartAsync();

        // a caller that already has a trace (the gateway, or a service in front of this one)
        var callersTrace = ActivityTraceId.CreateRandom().ToHexString();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/work");
        request.Headers.Add("traceparent", $"00-{callersTrace}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        request.Headers.Add(CorrelationContext.HeaderName, "from-client");
        using var response = await client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be(callersTrace);
        var line = sink.Events.Single();
        Property(line, "TraceId").Should().Be(callersTrace);
        line.Properties.Should().ContainKey("SpanId");
        Property(line, "CorrelationId").Should().Be("from-client");
    }

    // ── a real message ────────────────────────────────────────────────────────

    public record Hello { public string Text { get; init; } = ""; }

    private sealed class HelloConsumer(Serilog.ILogger logger, ConcurrentQueue<string> traces) : IConsumer<Hello>
    {
        public Task Consume(ConsumeContext<Hello> context)
        {
            logger.Information("handling {Text}", context.Message.Text);
            traces.Enqueue(Activity.Current!.TraceId.ToHexString());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ALogLineInsideAConsumer_HasTheTraceOfTheMessage_WhichContinuesThePublishersTrace()
    {
        using var spans = new Spans("MassTransit", Source.Name);
        var (logger, sink) = Create();
        var traces = new ConcurrentQueue<string>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(logger);
        services.AddSingleton(traces);
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<HelloConsumer>();
        });
        await using var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        string publishersTrace;
        using (var publisher = Source.StartActivity("publisher")!)
        {
            publishersTrace = publisher.TraceId.ToHexString();
            await harness.Bus.Publish(new Hello { Text = "hi" });
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<Hello>(timeout.Token)).Should().BeTrue();

        var line = sink.Events.Single();
        Property(line, "TraceId").Should().Be(traces.Single(), "the line is in the consumer's span");
        Property(line, "TraceId").Should().Be(publishersTrace, "the message carries the publisher's trace along");
        line.Properties.Should().ContainKey("SpanId");
    }
}
