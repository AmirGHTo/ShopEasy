using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.Shared.Contracts;
using System.Text.Json;

namespace ShopEasy.OrderService.ReadModels;

// Updates the OrderSummary read model in response to domain events.
// This is the CQRS projection — keeps the read side in sync with writes.
// Called directly from command handlers after successful writes.
public class OrderReadModelUpdater(
    OrderDbContext db,
    ILogger<OrderReadModelUpdater> logger)
{
    private readonly OrderDbContext _db = db;
    private readonly ILogger<OrderReadModelUpdater> _logger = logger;

    // Called when an order is first placed. The summary is only added to the context: the caller
    // saves it together with the order and its outbox entry, so an order can never exist without
    // its row in the read model (or the other way round).
    public void Stage(Order order, List<OrderItem> items)
    {
        var summary = new OrderSummary
        {
            OrderId = order.Id,
            UserId = order.UserId,
            UserEmail = order.UserEmail,
            StatusName = "Pending",
            StatusCode = (int)OrderStatus.Pending,
            TotalAmount = order.TotalAmount,
            AmountDisplay = order.TotalAmount.ToString("C"),
            ItemCount = items.Sum(i => i.Quantity),
            ItemsSummary = BuildItemsSummary(items),
            PaymentStatus = null,
            FailureReason = null,
            PlacedAt = order.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        _db.OrderSummaries.Add(summary);
    }

    // Called when saga status changes
    public async Task UpdateStatusAsync(
        Guid orderId,
        OrderStatus status,
        string? paymentStatus = null,
        string? failureReason = null)
    {
        var summary = await _db.OrderSummaries
            .FirstOrDefaultAsync(s => s.OrderId == orderId);

        if (summary is null)
        {
            _logger.LogWarning(
                "OrderSummary not found for Order {OrderId} — skipping update",
                orderId);
            return;
        }

        summary.StatusName = status.ToString();
        summary.StatusCode = (int)status;
        summary.PaymentStatus = paymentStatus ?? summary.PaymentStatus;
        summary.FailureReason = failureReason ?? summary.FailureReason;
        summary.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogDebug(
            "Read model updated for Order {OrderId} → {Status}",
            orderId, status);
    }

    private static string BuildItemsSummary(List<OrderItem> items)
    {
        var parts = items
            .Take(3) // show max 3 items
            .Select(i => $"{i.ProductName} x{i.Quantity}");

        var summary = string.Join(", ", parts);

        if (items.Count > 3)
            summary += $" +{items.Count - 3} more";

        return summary;
    }
}