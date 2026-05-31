using Prometheus;

namespace ShopEasy.InventoryService.Metrics;

public static class InventoryMetrics
{
    private static readonly Counter ReservationsSucceeded = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_inventory_reservations_total",
            "Total inventory reservations",
            new CounterConfiguration
            {
                LabelNames = new[] { "result" } // "success", "failed"
            });

    private static readonly Counter CacheHits = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_inventory_cache_hits_total",
            "Total Redis cache hits for inventory");

    private static readonly Counter CacheMisses = Prometheus.Metrics
        .CreateCounter(
            "shopeasy_inventory_cache_misses_total",
            "Total Redis cache misses for inventory");

    private static readonly Gauge LowStockProducts = Prometheus.Metrics
        .CreateGauge(
            "shopeasy_inventory_low_stock_products",
            "Number of products currently below low stock threshold");

    public static void RecordReservation(bool success)
        => ReservationsSucceeded.WithLabels(success ? "success" : "failed").Inc();

    public static void RecordCacheHit() => CacheHits.Inc();
    public static void RecordCacheMiss() => CacheMisses.Inc();

    public static void SetLowStockCount(int count)
        => LowStockProducts.Set(count);
}