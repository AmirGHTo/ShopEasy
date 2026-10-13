using ShopEasy.Shared.Services;
using ShopEasy.Shared.Grpc;

namespace ShopEasy.OrderService.Services;

// Provides stock info with Redis cache as primary source
// and gRPC as fallback.
//
// Consistency layers:
//   Layer 1: Redis cache hit    → ~1ms,   eventual (30s TTL)
//   Layer 2: Redis cache miss   → ~150ms, strong (gRPC → DB)
//   Layer 3: gRPC down          → stale cache (5min TTL)
//   Layer 4: Everything down    → reject order
//   Layer 5: Saga reservation   → authoritative check in InventoryService (row locking planned)
public class InventoryCacheService(
    CacheService cache,
    InventoryGrpcClient grpcClient,
    ILogger<InventoryCacheService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleTtl = TimeSpan.FromMinutes(5);

    private static string StaleKey(Guid productId) =>
        $"inventory:stale:{productId}";

    public async Task<StockInfo?> GetStockAsync(
        Guid productId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.Inventory(productId);

        // Layer 1 — Redis cache
        var cached = await cache.GetAsync<StockInfo>(cacheKey);
        if (cached is not null)
        {
            logger.LogDebug(
                "Stock cache HIT for {ProductId}: {Available} available",
                productId, cached.AvailableQuantity);
            return cached;
        }

        // Layer 2 — Cache miss, fetch via gRPC
        logger.LogDebug(
            "Stock cache MISS for {ProductId} — fetching via gRPC", productId);

        try
        {
            var response = await grpcClient.GetStockAsync(productId, ct);
            if (response is null) return null;

            var stockInfo = new StockInfo(
                ProductId: productId,
                ProductName: response.ProductName,
                AvailableQuantity: response.AvailableQty,
                IsLowStock: response.IsLowStock,
                Quantity: response.Quantity,
                ReservedQty: response.ReservedQty,
                IsStale: false,
                CachedAt: DateTime.UtcNow);

            await cache.SetAsync(cacheKey, stockInfo, CacheTtl);
            await cache.SetAsync(StaleKey(productId), stockInfo, StaleTtl);

            return stockInfo;
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogWarning(ex,
                "gRPC unavailable for {ProductId} — checking stale cache",
                productId);

            // Layer 3 — stale cache fallback
            var stale = await cache.GetAsync<StockInfo>(StaleKey(productId));
            if (stale is not null)
            {
                var age = (DateTime.UtcNow - stale.CachedAt).TotalSeconds;
                logger.LogWarning(
                    "Using stale cache for {ProductId} ({Age}s old). " +
                    "Saga will verify actual stock.",
                    productId, (int)age);
                return stale with { IsStale = true };
            }

            logger.LogError(
                "No stock data for {ProductId} — gRPC down, no cache. " +
                "Rejecting order.", productId);
            return null;
        }
    }

    public async Task<List<StockInfo>> GetStockBatchAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var results = new List<StockInfo>();
        var cacheMiss = new List<Guid>();

        // Check cache for each product
        foreach (var productId in productIds)
        {
            var cached = await cache.GetAsync<StockInfo>(CacheKeys.Inventory(productId));
            if (cached is not null)
                results.Add(cached);
            else
                cacheMiss.Add(productId);
        }

        if (!cacheMiss.Any())
        {
            logger.LogDebug(
                "Stock batch cache HIT — all {Count} products in cache",
                results.Count);
            return results;
        }

        logger.LogDebug(
            "Stock batch cache MISS for {MissCount}/{Total} products — gRPC fetch",
            cacheMiss.Count, results.Count + cacheMiss.Count);

        try
        {
            var grpcResults = await grpcClient.GetStockBatchAsync(cacheMiss, ct);

            foreach (var response in grpcResults)
            {
                if (!Guid.TryParse(response.ProductId, out var productId)) continue;

                var stockInfo = new StockInfo(
                    ProductId: productId,
                    ProductName: response.ProductName,
                    AvailableQuantity: response.AvailableQty,
                    IsLowStock: response.IsLowStock,
                    Quantity: response.Quantity,
                    ReservedQty: response.ReservedQty,
                    IsStale: false,
                    CachedAt: DateTime.UtcNow);

                await cache.SetAsync(
                    CacheKeys.Inventory(productId), stockInfo, CacheTtl);
                await cache.SetAsync(
                    StaleKey(productId), stockInfo, StaleTtl);

                results.Add(stockInfo);
            }
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogWarning(ex,
                "gRPC batch unavailable — falling back to stale cache " +
                "for {Count} products", cacheMiss.Count);

            foreach (var productId in cacheMiss)
            {
                var stale = await cache.GetAsync<StockInfo>(StaleKey(productId));
                if (stale is not null)
                {
                    var age = (DateTime.UtcNow - stale.CachedAt).TotalSeconds;
                    logger.LogWarning(
                        "Stale cache for {ProductId} ({Age}s old)",
                        productId, (int)age);
                    results.Add(stale with { IsStale = true });
                }
            }
        }

        return results;
    }
}