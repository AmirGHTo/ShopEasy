using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShopEasy.UnitTests.Infrastructure;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The services subscribe OpenTelemetry to the activity sources "MassTransit" and "Npgsql"
// (AddSource in each Program.cs) so Jaeger shows a span per message and per database command.
// A wrong or renamed source name would silently produce no spans, so check the libraries really
// emit under these names.
public class TracingSourceTests
{
    // Records the activities started by the named sources while it is alive
    private sealed class Spans : IDisposable
    {
        private readonly ActivityListener _listener;
        public ConcurrentQueue<Activity> Started { get; } = new();

        public Spans(params string[] sources)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => sources.Contains(source.Name),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => Started.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    public record Hello { public string Text { get; init; } = ""; }

    private sealed class HelloConsumer : IConsumer<Hello>
    {
        public Task Consume(ConsumeContext<Hello> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task MassTransit_EmitsASpanPerMessage_UnderTheSourceTheServicesSubscribeTo()
    {
        using var spans = new Spans("MassTransit");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMassTransitTestHarness(x => x.AddConsumer<HelloConsumer>());
        await using var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new Hello { Text = "hi" });

        // An explicit timeout: without one the harness gives up after a short default, which a
        // busy CI runner (the whole suite runs in parallel) can exceed — that made this test flaky.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<Hello>(timeout.Token)).Should().BeTrue();

        spans.Started.Should().NotBeEmpty("MassTransit traces sends and consumes");
        spans.Started.Select(a => a.Source.Name).Distinct().Should().Equal("MassTransit");
    }

    [RequiresPostgresFact]
    public async Task Npgsql_EmitsASpanPerCommand_UnderTheSourceTheServicesSubscribeTo()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("trace");
        using var spans = new Spans("Npgsql");

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
        }

        // the span is named after the database; the statement is a tag
        spans.Started.Should().Contain(a =>
            a.Source.Name == "Npgsql" && (a.GetTagItem("db.statement") as string) == "SELECT 1");
    }
}
