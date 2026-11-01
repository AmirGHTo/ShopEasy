using System.Collections.Concurrent;
using System.Diagnostics;

namespace ShopEasy.UnitTests.Support;

// Makes the named activity sources hand out spans (what OpenTelemetry's AddSource does in the services)
// and keeps the ones that were started, so that a test can look at their ids and tags.
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

    public void Dispose() => _listener.Dispose();
}
