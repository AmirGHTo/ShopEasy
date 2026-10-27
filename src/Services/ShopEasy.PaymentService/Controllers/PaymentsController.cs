using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Security;

namespace ShopEasy.PaymentService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = PolicyNames.PaymentRead)]
public class PaymentsController : ControllerBase
{
    private readonly PaymentDbContext _db;

    public PaymentsController(PaymentDbContext db) => _db = db;

    // ── GET /api/payments/order/{orderId} ─────────────────────────────────────
    // Users can only see their own payments; admins can see any
    [HttpGet("order/{orderId:guid}")]
    public async Task<IActionResult> GetByOrder(Guid orderId)
    {
        var userId  = User.FindFirst("sub")?.Value;
        var isAdmin = User.IsInRole("admin");

        var payment = await _db.Payments
            .FirstOrDefaultAsync(p => p.OrderId == orderId);

        if (payment is null) return NotFound();

        // Non-admins can only view their own payments
        if (!isAdmin && payment.UserId != userId)
            return Forbid();

        return Ok(payment);
    }

    // ── GET /api/payments/audit?orderIds=...&orderIds=... ─────────────────────
    // For the Order service's consistency check (a token the service got for itself, not a user's):
    // what is held for each order. Every payment row of the orders is returned, so a second payment
    // for one order shows; an order without a payment is simply not in the answer.
    public const int MaxAuditOrders = 100;

    [HttpGet("audit")]
    [Authorize(Policy = PolicyNames.PaymentInternal)]
    public async Task<IActionResult> Audit([FromQuery] Guid[] orderIds)
    {
        if (orderIds.Length is 0 or > MaxAuditOrders)
            return BadRequest(new { error = $"Ask for 1 to {MaxAuditOrders} orders at a time." });

        var payments = await _db.Payments
            .Where(p => orderIds.Contains(p.OrderId))
            .Select(p => new PaymentAuditEntry(p.OrderId, p.Status.ToString(), p.Amount))
            .ToListAsync();
        return Ok(payments);
    }

    // ── GET /api/payments ─────────────────────────────────────────────────────
    [HttpGet]
    [Authorize(Policy = PolicyNames.PaymentMonitor)]
    public async Task<IActionResult> GetAll()
    {
        var payments = await _db.Payments
            .OrderByDescending(p => p.CreatedAt)
            .Take(100)
            .ToListAsync();
        return Ok(payments);
    }
}

// The status is the PaymentStatus name (Pending, Succeeded, Failed, Refunded)
public record PaymentAuditEntry(Guid OrderId, string Status, decimal Amount);
