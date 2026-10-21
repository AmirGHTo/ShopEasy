using System.Diagnostics;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// Snapshot (HarnessExtensions) must show what the harness has seen, and must not wait for more:
// with AllowSlowRunners, Select<T>() waits out a 30 s inactivity timeout and can deadlock when
// that timeout fires while the enumeration ends.
public class HarnessSnapshotTests
{
    public record Ping { public Guid Id { get; init; } }

    private sealed class PingConsumer : IConsumer<Ping>
    {
        public Task Consume(ConsumeContext<Ping> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task Snapshot_ShowsPublishedAndConsumedMessages_AtOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<PingConsumer>();
        });
        var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.StartWithinLimitAsync();
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Ping { Id = Guid.NewGuid() });
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        (await harness.Consumed.Any<Ping>(wait.Token)).Should().BeTrue();

        var clock = Stopwatch.StartNew();
        harness.Published.Snapshot<Ping>().Should().ContainSingle();
        harness.Consumed.Snapshot<Ping>().Should().ContainSingle();
        harness.Published.Snapshot<Fault<Ping>>().Should().BeEmpty();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "Select<T>() would wait for the 30 s inactivity timeout");
    }
}
