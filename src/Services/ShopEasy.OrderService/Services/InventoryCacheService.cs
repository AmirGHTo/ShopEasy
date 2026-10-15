using ShopEasy.Shared.Services;

namespace ShopEasy.OrderService.Services;

// What the order endpoint needs from the stock cache: lets it be replaced in tests
public interface IInventoryStock
{
    Task<List<StockInfo>> GetStockBatchAsync(IEnumerable<Guid> productIds, CancellationToken ct = default);
}

// Provides stock info with Redis cache as primary source
// and the Inventory service as fallback.
//
// Consistency layers:
//   Layer 1: Redis cache hit    → ~1ms,   eventual (30s TTL)
//   Layer 2: Redis cache miss   → ~150ms, strong (gRPC → DB), one shared call per product
//   Layer 3: Inventory down     → stale cache (5min TTL)
//   Layer 4: Everything down    → reject order
//   Layer 5: Saga reservation   → authoritative check in InventoryService (row locking planned)
public class InventoryCacheService(
    CacheService cache,
    IStockSource source,
    StockFetchCoordinator coordinator,
    ILogger<InventoryCacheService> logger) : IInventoryStock
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleTtl = TimeSpan.FromMinutes(5);

    private static string StaleKey(Guid productId) =>
        $"inventory:stale:{productId}";

    public async Task<List<StockInfo>> GetStockBatchAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var results = new List<StockInfo>();
        var cacheMiss = new List<Guid>();

        // Check cache for each product
        foreach (var productId in productIds.Distinct())
        {
            var cached = await cache.GetAsync<StockInfo>(CacheKeys.Inventory(productId));
            if (cached is not null)
                results.Add(cached);
            else
                cacheMiss.Add(productId);
        }

        if (cacheMiss.Count == 0)
        {
            logger.LogDebug(
                "Stock batch cache HIT — all {Count} products in cache",
                results.Count);
            return results;
        }

        logger.LogDebug(
            "Stock batch cache MISS for {MissCount}/{Total} products — asking Inventory",
            cacheMiss.Count, results.Count + cacheMiss.Count);

        try
        {
            // Requests that miss the same product at the same time share one call to Inventory
            var fetched = await coordinator.FetchAsync(cacheMiss, FetchAndCacheAsync, ct);

            foreach (var productId in cacheMiss)
            {
                if (fetched.TryGetValue(productId, out var level) && level is not null)
                    results.Add(ToStockInfo(level));
            }
        }
        catch (InventoryUnavailableException ex)
        {
            logger.LogWarning(ex,
                "Inventory unavailable — falling back to stale cache " +
                "for {Count} products", cacheMiss.Count);

            foreach (var productId in cacheMiss)
            {
                // Layer 3 — stale cache fallback
                var stale = await cache.GetAsync<StockInfo>(StaleKey(productId));
                if (stale is not null)
                {
                    var age = (DateTime.UtcNow - stale.CachedAt).TotalSeconds;
                    logger.LogWarning(
                        "Stale cache for {ProductId} ({Age}s old). " +
                        "Saga will verify actual stock.",
                        productId, (int)age);
                    results.Add(stale with { IsStale = true });
                }
                else
                {
                    // Layer 4 — nothing to go on: the order endpoint rejects this product
                    logger.LogError(
                        "No stock data for {ProductId} — Inventory down, no cache. " +
                        "Rejecting order.", productId);
                }
            }
        }

        return results;
    }

    // The one call to Inventory for a group of products, and the cache writes that follow it
    private async Task<IReadOnlyList<StockLevel>> FetchAndCacheAsync(IReadOnlyList<Guid> productIds)
    {
        var levels = await source.GetStockBatchAsync(productIds);

        foreach (var level in levels)
        {
            var stockInfo = ToStockInfo(level);
            await cache.SetAsync(CacheKeys.Inventory(level.ProductId), stockInfo, CacheTtl);
            await cache.SetAsync(StaleKey(level.ProductId), stockInfo, StaleTtl);
        }

        return levels;
    }

    private static StockInfo ToStockInfo(StockLevel level) => new(
        ProductId: level.ProductId,
        ProductName: level.ProductName,
        AvailableQuantity: level.AvailableQuantity,
        IsLowStock: level.IsLowStock,
        Quantity: level.Quantity,
        ReservedQty: level.ReservedQuantity,
        IsStale: false,
        CachedAt: DateTime.UtcNow);
}
