namespace ShopEasy.Shared.Contracts;

// InventoryService → Saga: stock successfully reserved
public record InventoryReservedEvent
{
    public Guid OrderId { get; init; }
}

// InventoryService → Saga: not enough stock
public record InventoryReservationFailedEvent
{
    public Guid OrderId { get; init; }
    public string Reason { get; init; } = default!;
}

// InventoryService → Saga: reservation released (compensating action done)
public record InventoryReleasedEvent
{
    public Guid OrderId { get; init; }
}

// PaymentService → Saga: payment succeeded
public record PaymentSucceededEvent
{
    public Guid OrderId { get; init; }
    public Guid PaymentId { get; init; }
    public decimal Amount { get; init; }
}

// PaymentService → Saga: payment failed
public record PaymentFailedEvent
{
    public Guid OrderId { get; init; }
    public string Reason { get; init; } = default!;
}