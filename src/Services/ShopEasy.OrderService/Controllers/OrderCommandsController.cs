using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.OrderService.Controllers;

// ── Command side (CQRS) ───────────────────────────────────────────────────
// Write-only endpoints that modify state.
// Publishes events that update the read model asynchronously.
[ApiController]
[Route("api/orders")]
[Authorize]
[Produces("application/json")]

public class OrderCommandsController(
    OrderDbContext db,
    ILogger<OrderCommandsController> logger, InventoryCacheService inventoryCache, OrderReadModelUpdater readModelUpdater,
    IProductCatalogClient productCatalog,
    OrderCancellationService cancellation) : ControllerBase
{
    private readonly OrderDbContext _db = db;
    private readonly ILogger<OrderCommandsController> _logger = logger;
    private readonly InventoryCacheService _inventoryCache = inventoryCache;
    private readonly IProductCatalogClient _productCatalog = productCatalog;

    // ── POST /api/orders ──────────────────────────────────────────────────────
    // The client only chooses WHAT to buy (productId + quantity). Product names and
    // prices are always taken from the Product service — never from the request body.
    [HttpPost]
    [EnableRateLimiting("create-order")]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request)
    {
        var userId = User.FindFirst("sub")?.Value!;
        var userEmail = User.FindFirst("email")?.Value ?? "";

        // ── Step 0: Validate the request itself (shape, limits, duplicates) ───
        var validation = OrderRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Order rejected for User {UserId}: invalid request — {Errors}",
                userId, string.Join("; ", validation.Errors));
            return BadRequest(new
            {
                message = "Order could not be placed.",
                errors = validation.Errors
            });
        }

        // ── Step 0b: Resolve authoritative names and prices ───────────────────
        // If the Product service cannot answer we reject the order (HTTP 503) rather
        // than guess a price: charging a stale or client-supplied price is worse than
        // asking the caller to retry.
        IReadOnlyDictionary<Guid, CatalogProduct?> catalog;
        try
        {
            catalog = await _productCatalog.GetProductsAsync(
                validation.Lines.Select(l => l.ProductId), HttpContext.RequestAborted);
        }
        catch (ProductCatalogUnavailableException ex)
        {
            _logger.LogError(ex,
                "Order rejected for User {UserId}: Product service unavailable", userId);
            Response.Headers["Retry-After"] = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Product catalog is temporarily unavailable. Please retry shortly.",
                errors = Array.Empty<string>()
            });
        }

        var resolution = OrderLineResolver.Resolve(validation.Lines, catalog);
        if (!resolution.IsValid)
        {
            _logger.LogWarning(
                "Order rejected for User {UserId}: {Errors}",
                userId, string.Join("; ", resolution.Errors));
            return BadRequest(new
            {
                message = "Order could not be placed.",
                errors = resolution.Errors
            });
        }

        _logger.LogInformation(
            "User {UserId} placing order with {ItemCount} items",
            userId, resolution.Lines.Count);

        // ── Step 1: Validate stock via cache (eventual consistency) ───────────
        // Layer 1: Redis cache hit    → ~1ms,   30s TTL
        // Layer 2: Redis cache miss   → ~150ms, gRPC → DB
        // Layer 3: gRPC down          → stale cache, 5min TTL
        // Layer 4: Everything down    → reject order
        // Layer 5: Saga reservation   → authoritative check in InventoryService (row locking planned)
        var stockErrors = new List<string>();
        var hasStaleData = false;

        var productIds = resolution.Lines.Select(l => l.ProductId).ToList();
        var stockItems = await _inventoryCache.GetStockBatchAsync(
            productIds, HttpContext.RequestAborted);

        foreach (var item in resolution.Lines)
        {
            var stock = stockItems.FirstOrDefault(s => s.ProductId == item.ProductId);

            if (stock is null)
            {
                stockErrors.Add(
                    $"Product '{item.ProductName}' unavailable — " +
                    "inventory service temporarily unreachable.");
                continue;
            }

            if (stock.IsStale) hasStaleData = true;

            if (stock.AvailableQuantity < item.Quantity)
            {
                stockErrors.Add(
                    $"Insufficient stock for '{stock.ProductName}': " +
                    $"requested {item.Quantity}, available {stock.AvailableQuantity}.");
            }
        }

        if (stockErrors.Any())
        {
            _logger.LogWarning(
                "Order rejected for User {UserId}: {Errors}",
                userId, string.Join("; ", stockErrors));
            return BadRequest(new
            {
                message = "Order could not be placed.",
                errors = stockErrors
            });
        }

        if (hasStaleData)
            _logger.LogWarning(
                "Order accepted with stale inventory data for User {UserId}. " +
                "Saga will verify actual stock.", userId);

        // ── Step 2: Build the order ───────────────────────────────────────────
        var orderItems = resolution.Lines.Select(l => new OrderItem
        {
            Id = Guid.NewGuid(),
            ProductId = l.ProductId,
            ProductName = l.ProductName,   // from the Product service
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,       // from the Product service
        }).ToList();

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            UserEmail = userEmail,
            Status = OrderStatus.Pending,
            Notes = request.Notes?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = orderItems
        };
        order.TotalAmount = order.Items.Sum(i => i.LineTotal);

        // ── Step 3: Save order + outbox entry atomically ──────────────────────
        var eventData = new OrderConfirmedEvent
        {
            OrderId = order.Id,
            UserId = order.UserId,
            UserEmail = order.UserEmail,
            TotalAmount = order.TotalAmount,
            ConfirmedAt = DateTime.UtcNow,
            Items = order.Items.Select(i => new OrderLineItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
            }).ToList()
        };

        var outboxEntry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            EventType = nameof(OrderConfirmedEvent),
            Payload = System.Text.Json.JsonSerializer.Serialize(eventData),
            CreatedAt = DateTime.UtcNow
        };

        _db.Orders.Add(order);
        _db.OutboxEntries.Add(outboxEntry);
        await _db.SaveChangesAsync();

        // ── Step 4: Update CQRS read model ────────────────────────────────────
        // Updated synchronously so order appears immediately in GET /api/orders.
        // Subsequent status updates (Confirmed, Cancelled) happen via saga events.
        await readModelUpdater.CreateAsync(order, orderItems);

        _logger.LogInformation(
            "Order {OrderId} saved with status Pending. " +
            "Outbox entry created — Saga will orchestrate inventory + payment.",
            order.Id);

        OrderMetrics.RecordOrderPlaced("confirmed", order.TotalAmount);

        return CreatedAtAction(
            actionName: "GetById",
            controllerName: "OrderQueries",
            routeValues: new { orderId = order.Id },
            value: MapToResponse(order));
    }

    // ── DELETE /api/orders/{id} ───────────────────────────────────────────────
    // Cancels the order through the saga (see OrderCancellationService):
    //   204 cancelled / already cancelled · 202 accepted, stock release + refund in progress
    //   404 unknown order (or someone else's) · 409 too late: payment taken, order completing/completed
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (userId is null) return Unauthorized();

        var result = await cancellation.CancelAsync(
            id, userId, User.IsInRole("admin"), HttpContext.RequestAborted);

        switch (result.Outcome)
        {
            case CancelOutcome.NotFound:
                return NotFound();

            case CancelOutcome.NotCancellable:
                return Conflict(new { message = result.Message });

            case CancelOutcome.Cancelling:
                return Accepted(new { status = "Cancelling", message = result.Message });

            default: // Cancelled, AlreadyCancelled
                return NoContent();
        }
    }

    private static object MapToResponse(Order o) => new
    {
        o.Id,
        o.UserId,
        o.UserEmail,
        o.Status,
        o.TotalAmount,
        o.Notes,
        o.CreatedAt,
        o.UpdatedAt,
        Items = o.Items.Select(i => new
        {
            i.Id,
            i.ProductId,
            i.ProductName,
            i.Quantity,
            i.UnitPrice,
            i.LineTotal
        })
    };
}

// Request body for POST /api/orders.
// Items is nullable so that a missing/null list reaches OrderRequestValidator and
// produces a clean 400 message instead of a framework-generated one.
public record PlaceOrderRequest
{
    public List<OrderItemRequest?>? Items { get; init; }
    public string? Notes { get; init; }
}

// Only ProductId and Quantity are used.
// ProductName and UnitPrice are still accepted so existing clients keep working,
// but they are IGNORED: the Product service is the source of truth for both.
public record OrderItemRequest
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
    public string? ProductName { get; init; }
    public decimal? UnitPrice { get; init; }
}