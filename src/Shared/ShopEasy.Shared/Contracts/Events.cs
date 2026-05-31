namespace ShopEasy.Shared.Contracts;

// Published by OrderService after an order is confirmed
// Consumed by InventoryService and PaymentService
public record OrderConfirmedEvent
{
    public Guid OrderId { get; init; }
    public string UserId { get; init; } = default!;
    public string UserEmail { get; init; } = default!;
    public List<OrderLineItem> Items { get; init; } = new();
    public decimal TotalAmount { get; init; }
    public DateTime ConfirmedAt { get; init; }
}

public record OrderLineItem
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = default!;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

// Published by InventoryService when stock runs low
public record LowStockAlertEvent
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = default!;
    public int CurrentStock { get; init; }
    public int Threshold { get; init; }
    public DateTime DetectedAt { get; init; }
}

// Published by PaymentService after processing
public record PaymentProcessedEvent
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public string UserId { get; init; } = default!;
    public decimal Amount { get; init; }
    public PaymentStatus Status { get; init; }
    public string? FailureReason { get; init; }
    public DateTime ProcessedAt { get; init; }
}

public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3 
}