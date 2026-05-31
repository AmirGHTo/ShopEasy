using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;

namespace ShopEasy.OrderService.Controllers;

// ── Query side (CQRS) ─────────────────────────────────────────────────────
// Read-only endpoints that serve from the denormalized read model.
// Never touches the write database for reads.
// Fast, no joins, optimized for display.
[ApiController]
[Route("api/orders")]
[Authorize]
[Produces("application/json")]
public class OrderQueriesController(
    OrderDbContext db,
    ILogger<OrderQueriesController> logger) : ControllerBase
{
    private readonly OrderDbContext _db = db;
    private readonly ILogger<OrderQueriesController> _logger = logger;

    // ── GET /api/orders ───────────────────────────────────────────────────
    // Returns the current user's orders from the read model
    // Fast — no joins, pre-computed display values
    [HttpGet]
    public async Task<IActionResult> GetMyOrders(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var userId = User.FindFirst("sub")?.Value;
        if (userId is null) return Unauthorized();

        var query = _db.OrderSummaries
            .Where(o => o.UserId == userId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.StatusName == status);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(o => o.PlacedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        _logger.LogDebug(
            "User {UserId} queried orders — {Count} results",
            userId, items.Count);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items
        });
    }

    // ── GET /api/orders/{orderId} ─────────────────────────────────────────
    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> GetById(Guid orderId)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (userId is null) return Unauthorized();

        var summary = await _db.OrderSummaries
            .FirstOrDefaultAsync(o =>
                o.OrderId == orderId && o.UserId == userId);

        if (summary is null) return NotFound();

        return Ok(summary);
    }

    // ── GET /api/orders/status-counts ────────────────────────────────────
    // Returns count of orders by status for dashboard widgets
    [HttpGet("status-counts")]
    public async Task<IActionResult> GetStatusCounts()
    {
        var userId = User.FindFirst("sub")?.Value;
        if (userId is null) return Unauthorized();

        var counts = await _db.OrderSummaries
            .Where(o => o.UserId == userId)
            .GroupBy(o => o.StatusName)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        return Ok(counts);
    }
}