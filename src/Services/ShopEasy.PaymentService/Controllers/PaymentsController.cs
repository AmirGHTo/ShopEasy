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
