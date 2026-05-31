using ShopEasy.Shared.Grpc;

namespace ShopEasy.OrderService.Services;

// gRPC client for InventoryService
// Replaces the HTTP InventoryClient for internal stock checks
// 3-5x faster, strongly typed, compile-time contract enforcement
public class InventoryGrpcClient
{
    private readonly InventoryGrpc.InventoryGrpcClient _client;
    private readonly ILogger<InventoryGrpcClient> _logger;

    public InventoryGrpcClient(
        InventoryGrpc.InventoryGrpcClient client,
        ILogger<InventoryGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<StockResponse?> GetStockAsync(
        Guid productId,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug(
                "gRPC GetStock for product {ProductId}", productId);

            var response = await _client.GetStockAsync(
                new GetStockRequest { ProductId = productId.ToString() },
                cancellationToken: ct);

            return response.Found ? response : null;
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogError(ex,
                "gRPC GetStock failed for product {ProductId}: {Status}",
                productId, ex.Status);
            throw;
        }
    }

    public async Task<List<StockResponse>> GetStockBatchAsync(
        IEnumerable<Guid> productIds,
        CancellationToken ct = default)
    {
        try
        {
            var request = new GetStockBatchRequest();
            request.ProductIds.AddRange(productIds.Select(id => id.ToString()));

            _logger.LogDebug(
                "gRPC GetStockBatch for {Count} products",
                request.ProductIds.Count);

            var response = await _client.GetStockBatchAsync(
                request, cancellationToken: ct);

            return response.Items.ToList();
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogError(ex,
                "gRPC GetStockBatch failed: {Status}", ex.Status);
            throw;
        }
    }
}