using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The outbox delivery worker asks IBrokerReadiness before it publishes. The real implementation
// reads MassTransit's own bus health check, so these tests register a stand-in check with the
// same name and tag that AddMassTransit registers ("masstransit-bus", tag "masstransit").
public class BrokerReadinessTests
{
    private static (MassTransitBrokerReadiness Readiness, ListLogger<MassTransitBrokerReadiness> Log) Create(
        HealthStatus? busStatus, bool addUnrelatedFailingCheck = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var checks = services.AddHealthChecks();

        if (busStatus is { } status)
            checks.AddCheck("masstransit-bus", () => new HealthCheckResult(status),
                tags: ["ready", MassTransitBrokerReadiness.HealthCheckTag]);

        // e.g. the Identity service being down must not stop outbox delivery
        if (addUnrelatedFailingCheck)
            checks.AddCheck("identity", () => HealthCheckResult.Unhealthy(), tags: ["ready"]);

        var provider = services.BuildServiceProvider();
        var log = new ListLogger<MassTransitBrokerReadiness>();
        return (new MassTransitBrokerReadiness(
            provider.GetRequiredService<HealthCheckService>(), log), log);
    }

    [Fact]
    public async Task WhenTheBusIsHealthy_IsReady()
    {
        var (readiness, _) = Create(HealthStatus.Healthy);
        (await readiness.IsReadyAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded)]
    public async Task WhenTheBusIsNotFullyHealthy_IsNotReady(HealthStatus status)
    {
        var (readiness, _) = Create(status);
        (await readiness.IsReadyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task OtherFailingHealthChecks_DoNotBlockDelivery()
    {
        var (readiness, _) = Create(HealthStatus.Healthy, addUnrelatedFailingCheck: true);
        (await readiness.IsReadyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutAMassTransitHealthCheck_FailsClosed_AndSaysSoOnce()
    {
        var (readiness, log) = Create(busStatus: null);

        (await readiness.IsReadyAsync()).Should().BeFalse();
        (await readiness.IsReadyAsync()).Should().BeFalse();

        log.Messages(Microsoft.Extensions.Logging.LogLevel.Error).Should().HaveCount(1);
    }

    // Guards the real wiring: if a MassTransit upgrade renamed the health check or its tag, the
    // gate would fail closed and outbox delivery would stop. The in-memory transport behaves
    // like RabbitMQ for this purpose (the check reports on bus and receive-endpoint state).
    [Fact]
    public async Task UsesTheHealthCheckThatMassTransitActuallyRegisters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks();
        services.AddSingleton<IBrokerReadiness, MassTransitBrokerReadiness>();
        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<NoopConsumer>();
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
        });
        await using var provider = services.BuildServiceProvider(true);
        var readiness = provider.GetRequiredService<IBrokerReadiness>();

        // Register the check-bearing bus, but do not start it yet
        (await readiness.IsReadyAsync()).Should().BeFalse("the bus has not started");

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        (await readiness.IsReadyAsync()).Should().BeTrue("the bus and its endpoints are up");
    }

    private sealed class NoopConsumer : IConsumer<NoopMessage>
    {
        public Task Consume(ConsumeContext<NoopMessage> context) => Task.CompletedTask;
    }

    public record NoopMessage;
}
