using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace ShopEasy.UnitTests.Support;

// Activity sources and their listeners belong to the whole test process, not to a test. While a listener is
// attached, the spans that every test running at the same time starts are heard by it, and a test that
// expects NO listener sees one it did not attach. The tests that attach a SpanListener (or check that
// nothing is listening) therefore run alone, one after the other, in this collection.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpansCollection
{
    public const string Name = "Spans";
}

// Makes the named activity sources hand out spans (what OpenTelemetry's AddSource does in the services)
// and keeps the ones that were started, so that a test can look at their ids and tags.
// Use it only in a class marked [Collection(SpansCollection.Name)].
internal sealed class SpanListener : IDisposable
{
    private readonly ActivityListener _listener;
    public ConcurrentQueue<Activity> Started { get; } = new();

    public SpanListener(params string[] sources)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => Started.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IEnumerable<Activity> Named(string name) => Started.Where(a => a.OperationName == name);

    // A listener hears every span of the test process, and the tests of other classes run in parallel
    // with this one: a test that asserts on "the" saga.orchestrate span must ask for the one of its own order.
    public IEnumerable<Activity> Named(string name, Guid orderId) =>
        Named(name).Where(a => a.GetTagItem("order.id")?.ToString() == orderId.ToString());

    public void Dispose() => _listener.Dispose();
}
