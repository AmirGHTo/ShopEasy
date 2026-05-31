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
    ILogger<OrderCommandsController> logger, InventoryCacheService inventoryCache, OrderReadModelUpdater readModelUpdater) : ControllerBase
{
    private readonly OrderDbContext _db = db;
    private readonly ILogger<OrderCommandsController> _logger = logger;
    private readonly InventoryCacheService _inventoryCache = inventoryCache;

    // ── POST /api/orders ──────────────────────────────────────────────────────
    [HttpPost]
    [EnableRateLimiting("create-order")]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request)
    {
        var userId = User.FindFirst("sub")?.Value!;
        var userEmail = User.FindFirst("email")?.Value ?? "";

        _logger.LogInformation(
            "User {UserId} placing order with {ItemCount} items",
            userId, request.Items.Count);

        // ── Step 1: Validate stock via cache (eventual consistency) ───────────
        // Layer 1: Redis cache hit    → ~1ms,   30s TTL
        // Layer 2: Redis cache miss   → ~150ms, gRPC → DB
        // Layer 3: gRPC down          → stale cache, 5min TTL
        // Layer 4: Everything down    → reject order
        // Layer 5: Saga reservation   → always strong (SELECT FOR UPDATE)
        var stockErrors = new List<string>();
        var hasStaleData = false;

        var productIds = request.Items.Select(i => i.ProductId).ToList();
        var stockItems = await _inventoryCache.GetStockBatchAsync(
            productIds, HttpContext.RequestAborted);

        foreach (var item in request.Items)
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
        var orderItems = request.Items.Select(i => new OrderItem
        {
            Id = Guid.NewGuid(),
            ProductId = i.ProductId,
            ProductName = i.ProductName,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
        }).ToList();

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            UserEmail = userEmail,
            Status = OrderStatus.Pending,
            Notes = request.Notes,
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
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = User.FindFirst("sub")?.Value;
        var isAdmin = User.IsInRole("admin");

        var order = await _db.Orders.FindAsync(id);
        if (order is null) return NotFound();
        if (!isAdmin && order.UserId != userId) return Forbid();

        if (order.Status == OrderStatus.Shipped ||
            order.Status == OrderStatus.Delivered)
            return BadRequest(new
            {
                message = "Cannot cancel an order that has already shipped."
            });

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return NoContent();
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

public record PlaceOrderRequest(
    List<OrderItemRequest> Items,
    string? Notes);

public record OrderItemRequest(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);