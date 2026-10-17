using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShopEasy.Shared.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The retry policy the services put on their buses (ConsumerResilience), on a real MassTransit
// pipeline (in-memory transport). The pauses are shortened to milliseconds; everything else —
// which failures are retried, how often, and when the message is given up on — is as in production.
public class ConsumerResilienceTests
{
    public record Job { public Guid Id { get; init; } }

    // A consumer whose first N attempts throw whatever the test hands it
    private sealed class Script
    {
        private int _attempts;
        public int FailFirst { get; set; }
        public Func<Exception> Failure { get; set; } = () => new ApplicationException("boom");
        public int Attempts => _attempts;
        public TaskCompletionSource Succeeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Next()
        {
            if (Interlocked.Increment(ref _attempts) <= FailFirst) throw Failure();
            Succeeded.TrySetResult();
        }
    }

    private sealed class JobConsumer(Script script) : IConsumer<Job>
    {
        public Task Consume(ConsumeContext<Job> context)
        {
            script.Next();
            return Task.CompletedTask;
        }
    }

    private static ConsumerResilienceOptions Fast(int transientLimit = 12) => TestResilience.FastConsumers(transientLimit);

    private static async Task<(ServiceProvider Provider, ITestHarness Harness)> StartAsync(
        Script script, ConsumerResilienceOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(script);
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<JobConsumer>();
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.UseConsumerResilience(options);
                cfg.ConfigureEndpoints(ctx);
            });
        });

        var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.StartWithinLimitAsync();
        return (provider, harness);
    }

    private static CancellationToken Within(int seconds = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static NpgsqlException DatabaseDown() => new("the database is restarting", new IOException("connection reset"));

    private static async Task<bool> FaultedAsync(ITestHarness harness) =>
        await harness.Published.Any<Fault<Job>>(Within());

    [Fact]
    public async Task ADatabaseOutageLongerThanTheOldRetryWindow_IsRiddenOut_AndNothingIsDeadLettered()
    {
        // six failed attempts: the previous policy (three retries) gave up after the fourth
        var script = new Script { FailFirst = 6, Failure = DatabaseDown };
        var (provider, harness) = await StartAsync(script, Fast());
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });
        await script.Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(20));

        script.Attempts.Should().Be(7);
        harness.Published.Select<Fault<Job>>().Should().BeEmpty("the message was never given up on");
    }

    [Fact]
    public async Task ATransientFaultThatEntityFrameworkWrapped_IsStillRecognised()
    {
        var script = new Script { FailFirst = 5, Failure = () => new DbUpdateException("save failed", DatabaseDown()) };
        var (provider, harness) = await StartAsync(script, Fast());
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });
        await script.Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(20));

        script.Attempts.Should().Be(6);
    }

    [Fact]
    public async Task ADatabaseThatNeverComesBack_IsGivenUpOn_AfterExactlyTheConfiguredAttempts()
    {
        var script = new Script { FailFirst = int.MaxValue, Failure = DatabaseDown };
        var (provider, harness) = await StartAsync(script, Fast(transientLimit: 5));
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });

        (await FaultedAsync(harness)).Should().BeTrue("after the patient retries the message goes to the error queue");
        // 1 first attempt + 5 patient retries; the quick retries must not multiply on top of them
        script.Attempts.Should().Be(6);
    }

    [Fact]
    public async Task AFailureThatWillRepeat_GetsTheQuickRetries_ThenFaults()
    {
        var script = new Script { FailFirst = int.MaxValue, Failure = () => new ApplicationException("a bug") };
        var (provider, harness) = await StartAsync(script, Fast());
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });

        (await FaultedAsync(harness)).Should().BeTrue();
        script.Attempts.Should().Be(4, "the first attempt and three retries, not the twelve of an outage");
    }

    [Fact]
    public async Task ABrokenBusinessRule_IsNotRetriedAtAll()
    {
        var script = new Script { FailFirst = int.MaxValue, Failure = () => new InvalidOperationException("rule") };
        var (provider, harness) = await StartAsync(script, Fast());
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });

        (await FaultedAsync(harness)).Should().BeTrue();
        script.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task ARaceThatResolvesOnTheSecondTry_StillSucceedsQuickly()
    {
        var script = new Script { FailFirst = 1, Failure = () => new ApplicationException("unique key conflict with the twin message") };
        var (provider, harness) = await StartAsync(script, Fast());
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });
        await script.Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(20));

        script.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task WithThePatientRetryDisabled_OutagesFallBackToTheQuickRetries()
    {
        var script = new Script { FailFirst = int.MaxValue, Failure = DatabaseDown };
        var (provider, harness) = await StartAsync(script, Fast(transientLimit: 0));
        await using var _ = provider.StopWithinLimit();

        await harness.PublishWithinLimitAsync(new Job { Id = Guid.NewGuid() });

        (await FaultedAsync(harness)).Should().BeTrue();
        script.Attempts.Should().Be(4);
    }

    // ── the schedule itself ──────────────────────────────────────────────────

    [Fact]
    public void TheDefaultSchedule_DoublesFromOneSecondToAMinute_AndCoversSeveralMinutes()
    {
        var options = new ConsumerResilienceOptions { JitterRatio = 0 };

        var intervals = options.TransientIntervals();

        intervals.Select(i => i.TotalSeconds).Should().Equal(1, 2, 4, 8, 16, 32, 60, 60, 60, 60, 60, 60);
        intervals.Sum(i => i.TotalMinutes).Should().BeGreaterThan(6, "a database failover has to fit in the window");
    }

    [Fact]
    public void Jitter_StaysWithinItsBounds_AndDiffersBetweenProcesses()
    {
        var options = new ConsumerResilienceOptions { JitterRatio = 0.2 };

        var first = options.TransientIntervals(new Random(1));
        var second = options.TransientIntervals(new Random(2));

        for (var i = 0; i < first.Length; i++)
        {
            var nominal = Math.Min(Math.Pow(2, i), 60);
            first[i].TotalSeconds.Should().BeInRange(nominal * 0.8, nominal * 1.2);
        }

        first.Should().NotEqual(second, "replicas should not retry in lock-step");
    }

    [Theory]
    [InlineData(-1, 1, 60, 0.2)]
    [InlineData(5, 0, 60, 0.2)]
    [InlineData(5, 30, 10, 0.2)]
    [InlineData(5, 1, 60, 1.0)]
    [InlineData(5, 1, 60, -0.1)]
    public void ANonsensicalSchedule_FailsAtStartup(int limit, double min, double max, double jitter)
    {
        var options = new ConsumerResilienceOptions
        {
            TransientRetryLimit = limit, TransientMinIntervalSeconds = min,
            TransientMaxIntervalSeconds = max, JitterRatio = jitter
        };

        var act = () => options.TransientIntervals();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Resilience:Consumers*");
    }
}
