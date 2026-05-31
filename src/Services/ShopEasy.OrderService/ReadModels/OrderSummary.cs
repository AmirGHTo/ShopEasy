namespace ShopEasy.OrderService.ReadModels;

// Denormalized read model for order list queries.
// Updated asynchronously by consuming domain events.
// Never written to directly — only updated via event consumers.
public class OrderSummary
{
    public Guid OrderId { get; set; }
    public string UserId { get; set; } = default!;
    public string UserEmail { get; set; } = default!;
    public string StatusName { get; set; } = default!; // "Pending", "Confirmed"
    public int StatusCode { get; set; }
    public decimal TotalAmount { get; set; }
    public string AmountDisplay { get; set; } = default!; // "$100.00"
    public int ItemCount { get; set; }
    public string ItemsSummary { get; set; } = default!; // "4K Webcam x2, Mouse x1"
    public string? PaymentStatus { get; set; }             // "Succeeded", "Refunded"
    public string? FailureReason { get; set; }
    public DateTime PlacedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}