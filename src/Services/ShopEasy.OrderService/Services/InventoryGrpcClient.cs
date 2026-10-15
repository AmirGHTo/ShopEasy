using ShopEasy.Shared.Grpc;

namespace ShopEasy.OrderService.Services;

// gRPC client for the Inventory service: the internal stock check (a binary, typed, batched
// call). Every call goes through InventoryCallPolicy (deadline, retry after connection failures,
// circuit breaker) and reports failure as InventoryUnavailableException.
public class InventoryGrpcClient(
    InventoryGrpc.InventoryGrpcClient client,
    InventoryCallPolicy policy,
    ILogger<InventoryGrpcClient> logger) : IStockSource
{
    public async Task<IReadOnlyList<StockLevel>> GetStockBatchAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var request = new GetStockBatchRequest();
        request.ProductIds.AddRange(productIds.Select(id => id.ToString()));

        logger.LogDebug("gRPC GetStockBatch for {Count} products", request.ProductIds.Count);

        var response = await policy.ExecuteAsync(
            (deadline, token) => client.GetStockBatchAsync(request, deadline: deadline, cancellationToken: token).ResponseAsync,
            ct);

        return response.Items
            .Where(item => item.Found && Guid.TryParse(item.ProductId, out _))
            .Select(item => new StockLevel(
                Guid.Parse(item.ProductId), item.ProductName, item.Quantity,
                item.ReservedQty, item.AvailableQty, item.IsLowStock))
            .ToList();
    }
}
