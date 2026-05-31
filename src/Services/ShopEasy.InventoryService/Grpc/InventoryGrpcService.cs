using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Grpc;

namespace ShopEasy.InventoryService.Grpc;

// gRPC service implementation — handles internal service-to-service calls
// Much faster than HTTP/JSON for high-frequency stock checks
public class InventoryGrpcService(
    InventoryDbContext db,
    ILogger<InventoryGrpcService> logger) : InventoryGrpc.InventoryGrpcBase
{
    private readonly InventoryDbContext _db = db;
    private readonly ILogger<InventoryGrpcService> _logger = logger;

    // Single product stock check
    public override async Task<StockResponse> GetStock(
        GetStockRequest request,
        ServerCallContext context)
    {
        _logger.LogDebug(
            "gRPC GetStock called for product {ProductId}", request.ProductId);

        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Invalid product ID format: {request.ProductId}"));
        }

        var item = await _db.InventoryItems
            .FirstOrDefaultAsync(
                i => i.ProductId == productId,
                context.CancellationToken);

        if (item is null)
        {
            return new StockResponse
            {
                ProductId = request.ProductId,
                Found = false
            };
        }

        return new StockResponse
        {
            ProductId = item.ProductId.ToString(),
            ProductName = item.ProductName,
            Quantity = item.Quantity,
            ReservedQty = item.ReservedQty,
            AvailableQty = item.AvailableQuantity,
            IsLowStock = item.Quantity <= item.LowStockThreshold,
            Found = true
        };
    }

    // Batch stock check — check multiple products in one call
    // More efficient than multiple individual calls
    public override async Task<GetStockBatchResponse> GetStockBatch(
        GetStockBatchRequest request,
        ServerCallContext context)
    {
        _logger.LogDebug(
            "gRPC GetStockBatch called for {Count} products",
            request.ProductIds.Count);

        var productIds = request.ProductIds
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var items = await _db.InventoryItems
            .Where(i => productIds.Contains(i.ProductId))
            .ToListAsync(context.CancellationToken);

        var response = new GetStockBatchResponse();

        foreach (var id in request.ProductIds)
        {
            if (!Guid.TryParse(id, out var productId))
            {
                response.Items.Add(new StockResponse
                {
                    ProductId = id,
                    Found = false
                });
                continue;
            }

            var item = items.FirstOrDefault(i => i.ProductId == productId);
            if (item is null)
            {
                response.Items.Add(new StockResponse
                {
                    ProductId = id,
                    Found = false
                });
                continue;
            }

            response.Items.Add(new StockResponse
            {
                ProductId = item.ProductId.ToString(),
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                ReservedQty = item.ReservedQty,
                AvailableQty = item.AvailableQuantity,
                IsLowStock = item.Quantity <= item.LowStockThreshold,
                Found = true
            });
        }

        return response;
    }
}