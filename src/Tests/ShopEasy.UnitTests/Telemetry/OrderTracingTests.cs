using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.OrderService.Services;
using ShopEasy.OrderService.Tracing;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Telemetry;

// One order, one trace. The request that places an order, the saga that starts from it, the commands
// the orchestrator sends and the answers that come back used to be three traces, because the trace
// stops where work waits in a database table (the outbox entry, the saga the orchestrator picks up on
// its next look). The context of the trace is now stored on the row, and the loops start their spans
// inside it. These tests follow the trace over each of those hand-overs, and check that "order.id" is
// on the spans, which is how Jaeger finds all of an order's traces.
public class OrderTracingTests : IDisposable
{
    private static readonly ActivitySource Test = new("ShopEasy.Tests.OrderTracing");

    private readonly SpanListener _spans = new(OrderSpans.SourceName, Test.Name);

    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose()
    {
        _db.Dispose();
        _spans.Dispose();
    }

    // The span of "the request" or "the consumer" that wrote a row; returns the context it leaves
    private static (string Context, Activity Span) SomeEarlierWork()
    {
        var span = Test.StartActivity("earlier work")!;
        return (OrderSpans.CurrentContext()!, span);
    }

    // ── remembering and continuing a trace ────────────────────────────────────

    [Fact]
    public void TheCurrentContext_IsTheTraceparentOfTheRunningSpan_AndNullWithoutOne()
    {
        OrderSpans.CurrentContext().Should().BeNull();

        using var span = Test.StartActivity("work")!;

        OrderSpans.CurrentContext().Should().Be(span.Id).And.MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$");
    }

    [Fact]
    public void ASpanForASaga_ContinuesTheTraceItsRowRemembers_AndSaysWhichOrderAndWhere()
    {
        var (context, earlier) = SomeEarlierWork();
        earlier.Stop();
        var saga = NewSaga(SagaStatus.ProcessingPayment, context);

        using var span = OrderSpans.StartForSaga("saga.timeout", saga)!;

        span.TraceId.Should().Be(earlier.TraceId);
        span.ParentSpanId.Should().Be(earlier.SpanId);
        span.GetTagItem("order.id").Should().Be(saga.OrderId.ToString());
        span.GetTagItem("saga.id").Should().Be(saga.Id.ToString());
        span.GetTagItem("saga.status").Should().Be("ProcessingPayment");
    }

    [Fact]
    public void ASagaThatRemembersNoTrace_StartsANewOne()
    {
        var saga = NewSaga(SagaStatus.Started, traceContext: null);

        using var span = OrderSpans.StartForSaga("saga.orchestrate", saga)!;

        span.ParentSpanId.ToString().Should().Be(default(ActivitySpanId).ToString(), "a root span");
        span.GetTagItem("order.id").Should().Be(saga.OrderId.ToString());
    }

    [Fact]
    public void WithNothingListening_NoSpanIsStarted_AndNothingBreaks()
    {
        _spans.Dispose();
        var saga = NewSaga(SagaStatus.Started, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var span = OrderSpans.StartForSaga("saga.orchestrate", saga);
        OrderSpans.TagOrder(saga.OrderId);
        OrderSpans.TagStatus(span, SagaStatus.Completed);
        OrderSpans.Fail(span, new InvalidOperationException("x"));

        span.Should().BeNull();
    }

    [Fact]
    public void AStoredContextThatIsNotATraceparent_IsIgnored_NotAFailure()
    {
        var saga = NewSaga(SagaStatus.Started, "not a traceparent");

        using var span = OrderSpans.StartForSaga("saga.orchestrate", saga)!;

        span.Should().NotBeNull();
        span.ParentSpanId.ToString().Should().Be(default(ActivitySpanId).ToString());
    }

    // ── the three hand-overs ──────────────────────────────────────────────────

    [Fact]
    public async Task PlacingAnOrder_StoresTheTraceOfTheRequestOnTheOutboxEntry_AndTagsTheRequestWithTheOrder()
    {
        var orders = new ShopEasy.UnitTests.Orders.OrderPlacementHarness();
        var request = ShopEasy.UnitTests.Orders.OrderPlacementHarness.Order(
            ShopEasy.UnitTests.Orders.OrderPlacementHarness.Desk, 1);

        string? requestContext;
        object? taggedWith;
        using (var requestSpan = Test.StartActivity("POST api/orders"))
        {
            requestContext = OrderSpans.CurrentContext();
            var outcome = await orders.PlaceAsync(_db, request, key: null, userId: "user-1");
            outcome.StatusCode.Should().Be(201);
            taggedWith = requestSpan!.GetTagItem("order.id");
        }

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.TraceContext.Should().Be(requestContext).And.NotBeNull();
        taggedWith.Should().Be((await _db.Orders.SingleAsync()).Id.ToString());
    }

    private static OrderSagaState NewSaga(SagaStatus status, string? traceContext) => new()
    {
        Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Status = status, UserId = "u", UserEmail = "u@test.com",
        TotalAmount = 20m, ItemsJson = "[]", TraceContext = traceContext,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task TheSagaStarter_RemembersTheTraceOfTheMessageThatStartedIt()
    {
        var orderId = Guid.NewGuid();
        var context = Substitute.For<ConsumeContext<OrderConfirmedEvent>>();
        context.Message.Returns(new OrderConfirmedEvent
        {
            OrderId = orderId, UserId = "u", UserEmail = "u@test.com", TotalAmount = 20m,
            ConfirmedAt = DateTime.UtcNow, Items = []
        });

        string? expected;
        using (Test.StartActivity("order-saga-starter receive"))
        {
            expected = OrderSpans.CurrentContext();
            await new OrderConfirmedSagaStarter(_db, NullLogger<OrderConfirmedSagaStarter>.Instance).Consume(context);
        }

        (await _db.SagaStates.SingleAsync(s => s.OrderId == orderId)).TraceContext.Should().Be(expected).And.NotBeNull();
    }

    [Fact]
    public async Task TheOrchestrator_SendsItsCommandInsideTheTraceOfTheOrder()
    {
        var (context, earlier) = SomeEarlierWork();
        earlier.Stop();
        var saga = await SeedStartedSagaAsync(context);

        var publish = Substitute.For<IPublishEndpoint>();
        string? traceOfTheCommand = null;
        publish.When(p => p.Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>()))
            .Do(_ => traceOfTheCommand = Activity.Current?.TraceId.ToHexString());

        await RunOrchestratorAsync(publish);

        traceOfTheCommand.Should().Be(earlier.TraceId.ToHexString(), "the command and everything after it belong to the order's trace");
        var span = _spans.Named("saga.orchestrate").Should().ContainSingle().Subject;
        span.TraceId.Should().Be(earlier.TraceId);
        span.GetTagItem("order.id").Should().Be(saga.OrderId.ToString());
        span.GetTagItem("saga.status").Should().Be("ReservingInventory", "where the saga is after this step");
    }

    [Fact]
    public async Task AFailingOrchestratorStep_MarksItsSpanAsAnError()
    {
        await SeedStartedSagaAsync(traceContext: null);
        var publish = Substitute.For<IPublishEndpoint>();
        publish.Publish(Arg.Any<ReserveInventoryCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker is down"));

        await RunOrchestratorAsync(publish);

        var span = _spans.Named("saga.orchestrate").Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().Be("broker is down");
    }

    [Fact]
    public async Task TheTimeoutService_CompensatesInsideTheTraceOfTheOrder()
    {
        var (context, earlier) = SomeEarlierWork();
        earlier.Stop();
        var saga = NewSaga(SagaStatus.ProcessingPayment, context);
        saga.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        _db.Orders.Add(new Order
        {
            Id = saga.OrderId, UserId = "u", UserEmail = "u@test.com", Status = OrderStatus.Pending,
            TotalAmount = 20m, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _db.OrderSummaries.Add(new OrderSummary
        {
            OrderId = saga.OrderId, UserId = "u", UserEmail = "u@test.com", StatusName = "Pending",
            StatusCode = 0, TotalAmount = 20m, AmountDisplay = "$20.00", ItemCount = 1,
            ItemsSummary = "Widget x1", PlacedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _db.SagaStates.Add(saga);
        await _db.SaveChangesAsync();

        var publish = Substitute.For<IPublishEndpoint>();
        string? traceOfTheCommand = null;
        publish.When(p => p.Publish(Arg.Any<ReleaseInventoryCommand>(), Arg.Any<CancellationToken>()))
            .Do(_ => traceOfTheCommand = Activity.Current?.TraceId.ToHexString());
        var timeout = new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance);

        await timeout.HandleTimedOutSaga(
            await _db.SagaStates.AsNoTracking().SingleAsync(s => s.Id == saga.Id),
            _db, publish, new InMemorySagaLocking(), CancellationToken.None);

        traceOfTheCommand.Should().Be(earlier.TraceId.ToHexString());
        var span = _spans.Named("saga.timeout").Should().ContainSingle().Subject;
        span.TraceId.Should().Be(earlier.TraceId);
        span.GetTagItem("order.id").Should().Be(saga.OrderId.ToString());
        span.GetTagItem("saga.status").Should().Be("ReleasingInventory");
    }

    private async Task<OrderSagaState> SeedStartedSagaAsync(string? traceContext)
    {
        var saga = NewSaga(SagaStatus.Started, traceContext);
        var now = DateTime.UtcNow;
        _db.Orders.Add(new Order
        {
            Id = saga.OrderId, UserId = "u", UserEmail = "u@test.com", Status = OrderStatus.Pending,
            TotalAmount = 20m, CreatedAt = now, UpdatedAt = now
        });
        _db.OrderSummaries.Add(new OrderSummary
        {
            OrderId = saga.OrderId, UserId = "u", UserEmail = "u@test.com", StatusName = "Pending",
            StatusCode = 0, TotalAmount = 20m, AmountDisplay = "$20.00", ItemCount = 1,
            ItemsSummary = "Widget x1", PlacedAt = now, UpdatedAt = now
        });
        _db.SagaStates.Add(saga);
        await _db.SaveChangesAsync();
        return saga;
    }

    private Task RunOrchestratorAsync(IPublishEndpoint publish)
    {
        var services = new ServiceCollection()
            .AddSingleton(_db).AddSingleton(publish).AddSingleton<ISagaLocking>(new InMemorySagaLocking())
            .BuildServiceProvider();
        return new SagaOrchestrator(
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance,
                Options.Create(new SagaOrchestratorOptions { MaxAttempts = 3 }))
            .ProcessPendingSagas(CancellationToken.None);
    }

    // ── order.id on the span of every message of the order ────────────────────

    public record Ping { public Guid OrderId { get; init; } }
    public record Unrelated { public string Text { get; init; } = ""; }

    private sealed class PingConsumer : IConsumer<Ping>
    {
        public Task Consume(ConsumeContext<Ping> context) => Task.CompletedTask;
    }

    private sealed class UnrelatedConsumer : IConsumer<Unrelated>
    {
        public Task Consume(ConsumeContext<Unrelated> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task ASpanOfAMessageOfAnOrder_CarriesItsOrderId_AndOneWithoutAnOrderDoesNot()
    {
        using var messaging = new SpanListener("MassTransit");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(typeof(CorrelationConsumeFilter<>));
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<PingConsumer>();
            x.AddConsumer<UnrelatedConsumer>();
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.UseConsumeFilter(typeof(CorrelationConsumeFilter<>), ctx);
                cfg.ConfigureEndpoints(ctx);
            });
        });
        await using var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var orderId = Guid.NewGuid();
        await harness.Bus.Publish(new Ping { OrderId = orderId });
        await harness.Bus.Publish(new Unrelated { Text = "hello" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<Ping>(timeout.Token)).Should().BeTrue();
        (await harness.Consumed.Any<Unrelated>(timeout.Token)).Should().BeTrue();

        var tagged = messaging.Started.Where(a => a.GetTagItem("order.id") is not null).ToList();
        tagged.Should().NotBeEmpty("the consumer's span says which order it works on");
        tagged.Select(a => a.GetTagItem("order.id")).Should().OnlyContain(id => (string?)id == orderId.ToString());
    }
}
