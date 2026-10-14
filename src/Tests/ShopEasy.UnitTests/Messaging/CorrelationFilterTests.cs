using System.Collections.Concurrent;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The correlation filters on a real MassTransit pipeline (in-memory transport): the id travels
// from whoever publishes, through a consumer, into the messages that consumer publishes.
public class CorrelationFilterTests
{
    public record Ping { public Guid OrderId { get; init; } }
    public record Pong { public Guid OrderId { get; init; } }

    // What the consumers saw
    private sealed class Recorder : ILogEventSink
    {
        public ConcurrentQueue<string?> PingCorrelation { get; } = new();
        public ConcurrentQueue<string?> PongHeader { get; } = new();
        public ConcurrentQueue<string?> PongCorrelation { get; } = new();
        public ConcurrentQueue<LogEvent> Logged { get; } = new();

        public Serilog.ILogger Logger { get; }

        public Recorder() => Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(this)
            .CreateLogger();

        public void Emit(LogEvent logEvent) => Logged.Enqueue(logEvent);
    }

    private sealed class PingConsumer(Recorder recorder) : IConsumer<Ping>
    {
        public async Task Consume(ConsumeContext<Ping> context)
        {
            recorder.PingCorrelation.Enqueue(CorrelationContext.Current);
            recorder.Logger.Information("handling ping");
            await context.Publish(new Pong { OrderId = context.Message.OrderId });
        }
    }

    private sealed class PongConsumer(Recorder recorder) : IConsumer<Pong>
    {
        public Task Consume(ConsumeContext<Pong> context)
        {
            recorder.PongHeader.Enqueue(context.Headers.Get<string>(CorrelationContext.HeaderName));
            recorder.PongCorrelation.Enqueue(CorrelationContext.Current);
            return Task.CompletedTask;
        }
    }

    private static async Task<(ServiceProvider Provider, ITestHarness Harness, Recorder Recorder)> StartAsync()
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddScoped(typeof(CorrelationConsumeFilter<>));
        services.AddScoped(typeof(CorrelationPublishFilter<>));
        services.AddScoped(typeof(CorrelationSendFilter<>));
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<PingConsumer>();
            x.AddConsumer<PongConsumer>();
            x.UsingInMemory((ctx, cfg) =>
            {
                // as in the services' Program.cs
                cfg.UseConsumeFilter(typeof(CorrelationConsumeFilter<>), ctx);
                cfg.UsePublishFilter(typeof(CorrelationPublishFilter<>), ctx);
                cfg.UseSendFilter(typeof(CorrelationSendFilter<>), ctx);
                cfg.ConfigureEndpoints(ctx);
            });
        });

        var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return (provider, harness, recorder);
    }

    private static async Task WaitForPongAsync(ITestHarness harness)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<Pong>(timeout.Token)).Should().BeTrue("the chain should complete");
    }

    [Fact]
    public async Task TheIdInTheHeader_IsRestoredInTheConsumer_AndStampedOnWhatItPublishes()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() },
            ctx => ctx.Headers.Set(CorrelationContext.HeaderName, "trace-1"));
        await WaitForPongAsync(harness);

        recorder.PingCorrelation.Should().Equal("trace-1");
        // the consumer never mentioned the id when publishing, yet the next message carries it
        recorder.PongHeader.Should().Equal("trace-1");
        recorder.PongCorrelation.Should().Equal("trace-1");
    }

    [Fact]
    public async Task APublishMadeWhileHandlingARequest_CarriesTheRequestsId()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        // what an HTTP controller does: the middleware has pushed the request's id
        using (CorrelationContext.Push("req-42"))
            await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() });
        await WaitForPongAsync(harness);

        recorder.PingCorrelation.Should().Equal("req-42");
        recorder.PongHeader.Should().Equal("req-42");
    }

    [Fact]
    public async Task ACommandSentWhileHandlingARequest_CarriesTheRequestsId()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        var endpoint = await harness.Bus.GetSendEndpoint(new Uri("queue:ping"));
        using (CorrelationContext.Push("send-7"))
            await endpoint.Send(new Ping { OrderId = Guid.NewGuid() });
        await WaitForPongAsync(harness);

        recorder.PingCorrelation.Should().Equal("send-7");
    }

    [Fact]
    public async Task WithoutAnId_NothingIsInvented()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() });
        await WaitForPongAsync(harness);

        recorder.PingCorrelation.Should().Equal((string?)null);
        recorder.PongHeader.Should().Equal((string?)null);
    }

    [Fact]
    public async Task AnIdTheSenderSetExplicitly_IsNotOverwrittenByTheAmbientOne()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        using (CorrelationContext.Push("ambient"))
            await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() },
                ctx => ctx.Headers.Set(CorrelationContext.HeaderName, "explicit"));
        await WaitForPongAsync(harness);

        recorder.PingCorrelation.Should().Equal("explicit");
    }

    [Fact]
    public async Task EveryLogLineOfAConsumer_CarriesTheCorrelationIdAndTheOrderId()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;
        var orderId = Guid.NewGuid();

        await harness.Bus.Publish(new Ping { OrderId = orderId },
            ctx => ctx.Headers.Set(CorrelationContext.HeaderName, "trace-9"));
        await WaitForPongAsync(harness);

        var line = recorder.Logged.Single(e => e.MessageTemplate.Text == "handling ping");
        line.Properties[CorrelationContext.LogProperty].ToString().Should().Be("\"trace-9\"");
        line.Properties["OrderId"].ToString().Should().Be(orderId.ToString());
    }

    [Fact]
    public async Task AMessageWithoutAnOrderId_AddsNoOrderIdProperty()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        await harness.Bus.Publish(new Ping { OrderId = Guid.Empty });
        await WaitForPongAsync(harness);

        recorder.Logged.Single(e => e.MessageTemplate.Text == "handling ping")
            .Properties.Should().NotContainKey("OrderId");
    }

    [Fact]
    public async Task TheIdDoesNotLeakIntoTheNextMessage()
    {
        var (provider, harness, recorder) = await StartAsync();
        await using var _ = provider;

        await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() },
            ctx => ctx.Headers.Set(CorrelationContext.HeaderName, "first"));
        await harness.Bus.Publish(new Ping { OrderId = Guid.NewGuid() });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (recorder.PingCorrelation.Count < 2 && !timeout.IsCancellationRequested)
            await Task.Delay(50);

        recorder.PingCorrelation.Should().BeEquivalentTo(new string?[] { "first", null });
    }
}
