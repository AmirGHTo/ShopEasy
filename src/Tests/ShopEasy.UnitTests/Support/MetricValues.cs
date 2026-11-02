using System.Globalization;
using System.Text;
using Xunit;

namespace ShopEasy.UnitTests.Support;

// Reads a metric as Prometheus sees it. The metrics are static and shared by the whole run, so a test
// reads a value before and after; the tests that do so run alone (MetricsCollection).
internal static class MetricValues
{
    // A series as exposed, e.g. shopeasy_x_total{a="b"}; 0 until it exists
    public static async Task<double> ReadAsync(string series)
        => await FindAsync(series) ?? 0;

    // Whether the series is exposed at all: a gauge that was never set is a gap, not a 0
    public static async Task<bool> ExistsAsync(string series)
        => await FindAsync(series) is not null;

    private static async Task<double?> FindAsync(string series)
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var line = Encoding.UTF8.GetString(stream.ToArray()).Split('\n')
            .FirstOrDefault(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return line is null ? null : double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsCollection
{
    public const string Name = "Metrics";
}
