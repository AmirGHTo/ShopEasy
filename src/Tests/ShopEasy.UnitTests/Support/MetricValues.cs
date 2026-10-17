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
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var line = Encoding.UTF8.GetString(stream.ToArray()).Split('\n')
            .FirstOrDefault(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return line is null ? 0 : double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsCollection
{
    public const string Name = "Metrics";
}
