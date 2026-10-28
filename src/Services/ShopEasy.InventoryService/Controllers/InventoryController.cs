using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.InventoryService.Metrics;
using ShopEasy.Shared.Services;
using ShopEasy.Shared.Security;

namespace ShopEasy.InventoryService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class InventoryController : ControllerBase
{
    private readonly InventoryDbContext _db;
    private readonly IInventoryLocking _locking;
    private readonly CacheService _cache;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(
        InventoryDbContext db,
        IInventoryLocking locking,
        CacheService cache,
        ILogger<InventoryController> logger)
    {
        _db = db;
        _locking = locking;
        _cache = cache;
        _logger = logger;
    }

    // ── GET /api/inventory ────────────────────────────────────────────────────
    [HttpGet]
    [Authorize(Policy = PolicyNames.InventoryRead)]
    public async Task<IActionResult> GetAll()
    {
        // Cache the full inventory list for 30 seconds
        // Short TTL because stock levels change with every order
        var cached = await _cache.GetAsync<List<InventoryItemResponse>>(CacheKeys.InventoryAll);
        if (cached is not null)
        {
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        var items = await _db.InventoryItems
            .OrderBy(i => i.ProductName)
            .Select(i => new InventoryItemResponse(
                i.ProductId,
                i.ProductName,
                i.Quantity,
                i.ReservedQty,
                i.AvailableQuantity,
                i.LowStockThreshold,
                i.UpdatedAt,
                i.Quantity <= i.LowStockThreshold))
            .ToListAsync();

        await _cache.SetAsync(CacheKeys.InventoryAll, items, TimeSpan.FromSeconds(30));
        Response.Headers["X-Cache"] = "MISS";

        return Ok(items);
    }

    // ── GET /api/inventory/{productId} ────────────────────────────────────────
    // Called synchronously by OrderService before confirming an order.
    // Short cache TTL (30s) — stock changes frequently.
    [HttpGet("{productId:guid}")]
    [Authorize(Policy = PolicyNames.InventoryRead)]
    public async Task<IActionResult> GetByProduct(Guid productId)
    {
        var cacheKey = CacheKeys.Inventory(productId);

        var cached = await _cache.GetAsync<InventoryItemResponse>(cacheKey);
        if (cached is not null)
        {
            InventoryMetrics.RecordCacheHit();
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        InventoryMetrics.RecordCacheMiss();
        var item = await _db.InventoryItems
            .FirstOrDefaultAsync(i => i.ProductId == productId);

        if (item is null)
            return NotFound(new { message = $"Product {productId} not found in inventory" });

        var result = new InventoryItemResponse(
            item.ProductId,
            item.ProductName,
            item.Quantity,
            item.ReservedQty,
            item.AvailableQuantity,
            item.LowStockThreshold,
            item.UpdatedAt,
            item.Quantity <= item.LowStockThreshold);

        await _cache.SetAsync(cacheKey, result, TimeSpan.FromSeconds(30));
        Response.Headers["X-Cache"] = "MISS";

        return Ok(result);
    }

    // ── POST /api/inventory/{productId}/adjust ────────────────────────────────
    // Admin only — manually adjust stock (e.g. receiving new shipment)
    [HttpPost("{productId:guid}/adjust")]
    [Authorize(Policy = PolicyNames.InventoryAdjust)]
    public async Task<IActionResult> Adjust(
        Guid productId, [FromBody] AdjustStockRequest request)
    {
        // Lock the row so the adjustment cannot interleave with a reservation / commit
        // (otherwise one of the two updates would silently overwrite the other).
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var item = (await _locking.LockItemsAsync(_db, new[] { productId })).FirstOrDefault();
        if (item is null) return NotFound();

        var previousQty = item.Quantity;
        item.Quantity += request.QuantityChange;
        item.Quantity = Math.Max(0, item.Quantity);
        item.UpdatedAt = DateTime.UtcNow;

        _db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            QuantityChange = request.QuantityChange,
            Reason = request.Reason,
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        // Invalidate both the specific product cache and the full list cache
        await _cache.InvalidateAsync(CacheKeys.Inventory(productId));
        await _cache.InvalidateAsync(CacheKeys.InventoryAll);

        _logger.LogInformation(
            "Stock adjusted for {ProductName}: {Previous} → {New} ({Change:+#;-#;0})",
            item.ProductName, previousQty, item.Quantity, request.QuantityChange);

        return Ok(new
        {
            item.ProductId,
            item.ProductName,
            item.Quantity,
            item.AvailableQuantity
        });
    }

    // ── GET /api/inventory/{productId}/transactions ───────────────────────────
    [HttpGet("{productId:guid}/transactions")]
    [Authorize(Policy = PolicyNames.InventoryAudit)]
    public async Task<IActionResult> GetTransactions(Guid productId)
    {
        // Transactions are audit data — not cached, always fresh
        var transactions = await _db.StockTransactions
            .Where(t => t.ProductId == productId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();

        return Ok(transactions);
    }
}

// ── Response record — used for serialization into Redis ──────────────────────
public record InventoryItemResponse(
    Guid ProductId,
    string ProductName,
    int Quantity,
    int ReservedQty,
    int AvailableQuantity,
    int LowStockThreshold,
    DateTime UpdatedAt,
    bool IsLowStock);

public record AdjustStockRequest(int QuantityChange, string Reason);