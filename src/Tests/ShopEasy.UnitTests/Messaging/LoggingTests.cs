using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using ShopEasy.Shared.Logging;
using ShopEasy.Shared.Messaging;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// What an operator reads: which service wrote the line, and which request it belongs to.
public class LoggingTests
{
    private sealed class Collector : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    // A logger with the production enrichers and levels, writing to memory instead of the console
    private static (Serilog.ILogger Logger, Collector Sink) Create(string service, string? environment = null)
    {
        var sink = new Collector();
        var logger = new LoggerConfiguration()
            .ApplyShopEasyDefaults(service, environment)
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    // The line exactly as the console shows it (without the timestamp, which varies)
    private static string Render(LogEvent logEvent)
    {
        var formatter = new MessageTemplateTextFormatter(
            ShopEasyLogging.ConsoleTemplate.Replace("{Timestamp:HH:mm:ss} ", ""));
        using var writer = new StringWriter();
        formatter.Format(logEvent, writer);
        return writer.ToString().TrimEnd();
    }

    [Theory]
    [InlineData("ShopEasy.OrderService")]
    [InlineData("ShopEasy.Gateway")]
    [InlineData("ShopEasy.Identity")]
    public void EachLineNamesTheServiceThatWroteIt(string service)
    {
        var (logger, sink) = Create(service);

        logger.Information("Order saved");

        Render(sink.Events.Single()).Should().Be($"[INF] [{service}] [-] Order saved");
    }

    [Fact]
    public void InsideARequestOrMessage_TheLineCarriesItsCorrelationId_AndOnlyThere()
    {
        var (logger, sink) = Create("ShopEasy.PaymentService");

        logger.Information("before");
        using (CorrelationContext.Push("a1b2c3d4"))
            logger.Information("during");
        logger.Information("after");

        sink.Events.Select(Render).Should().Equal(
            "[INF] [ShopEasy.PaymentService] [-] before",
            "[INF] [ShopEasy.PaymentService] [a1b2c3d4] during",
            "[INF] [ShopEasy.PaymentService] [-] after");
    }

    [Fact]
    public void TheEnvironmentIsAPropertyWhenGiven()
    {
        var (logger, sink) = Create("ShopEasy.OrderService", environment: "Production");

        logger.Information("hello");

        sink.Events.Single().Properties["Environment"].ToString().Should().Be("\"Production\"");
    }

    [Fact]
    public void LowLevelFrameworkNoiseIsFiltered()
    {
        var (logger, sink) = Create("ShopEasy.OrderService");

        logger.ForContext("SourceContext", "Microsoft.AspNetCore.Routing").Information("routing detail");
        logger.ForContext("SourceContext", "Microsoft.AspNetCore.Routing").Warning("a real problem");
        logger.ForContext("SourceContext", "ShopEasy.OrderService.Something").Information("our own line");

        sink.Events.Select(e => e.MessageTemplate.Text)
            .Should().Equal("a real problem", "our own line");
    }

    // ── Seq ──────────────────────────────────────────────────────────────────

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Fact]
    public void TheSeqUrlComesFromConfiguration()
        => ShopEasyLogging.ResolveSeqUrl(Config((ShopEasyLogging.SeqUrlKey, "http://logs.internal:5341")))
            .Should().Be("http://logs.internal:5341");

    [Fact]
    public void AnEmptySeqUrlSwitchesSeqOff()
        => ShopEasyLogging.ResolveSeqUrl(Config((ShopEasyLogging.SeqUrlKey, "")))
            .Should().BeEmpty();

    [Fact]
    public void WithoutAnyConfiguration_SeqDefaultsToTheComposeHostName()
    {
        // (an environment variable Seq__ServerUrl would win; the test environment does not set one)
        Environment.GetEnvironmentVariable("Seq__ServerUrl").Should().BeNull();

        ShopEasyLogging.ResolveSeqUrl(Config()).Should().Be(ShopEasyLogging.DefaultSeqUrl);
    }
}

public class CorrelationContextTests
{
    [Fact]
    public void PushMakesTheIdCurrent_UntilDisposed()
    {
        CorrelationContext.Current.Should().BeNull();

        using (CorrelationContext.Push("abc"))
            CorrelationContext.Current.Should().Be("abc");

        CorrelationContext.Current.Should().BeNull();
    }

    [Fact]
    public void ScopesNest_AndRestoreTheOuterId()
    {
        using (CorrelationContext.Push("outer"))
        {
            using (CorrelationContext.Push("inner"))
                CorrelationContext.Current.Should().Be("inner");

            CorrelationContext.Current.Should().Be("outer");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToCorrelateWith_ChangesNothing(string? id)
    {
        using (CorrelationContext.Push("kept"))
        using (CorrelationContext.Push(id))
            CorrelationContext.Current.Should().Be("kept");
    }

    [Fact]
    public async Task TheIdFollowsAsyncCode()
    {
        using (CorrelationContext.Push("async-id"))
        {
            await Task.Delay(10);
            await Task.Yield();
            CorrelationContext.Current.Should().Be("async-id");
        }
    }

    [Fact]
    public async Task ConcurrentWork_DoesNotSeeEachOthersIds()
    {
        async Task<string?> Work(string id)
        {
            using (CorrelationContext.Push(id))
            {
                await Task.Delay(20);
                return CorrelationContext.Current;
            }
        }

        var seen = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Work($"id-{i}")));

        seen.Should().Equal(Enumerable.Range(0, 20).Select(i => $"id-{i}"));
    }

    [Fact]
    public void NewIdsAreShortAndDifferent()
    {
        var a = CorrelationContext.NewId();
        var b = CorrelationContext.NewId();

        a.Should().HaveLength(8);
        a.Should().NotBe(b);
    }
}
