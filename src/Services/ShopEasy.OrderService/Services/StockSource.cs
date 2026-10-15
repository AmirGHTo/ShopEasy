namespace ShopEasy.OrderService.Services;

// What Inventory knows about one product's stock
public record StockLevel(
    Guid ProductId,
    string ProductName,
    int Quantity,
    int ReservedQuantity,
    int AvailableQuantity,
    bool IsLowStock);

// Where stock levels come from when they are not in the cache: the Inventory service. The cache
// service depends on this, not on gRPC, so how the call is made (deadlines, retries, circuit
// breaker) can change without touching it, and tests can replace it.
public interface IStockSource
{
    // Levels for the products Inventory knows about; unknown products are left out.
    // Throws InventoryUnavailableException when Inventory cannot answer.
    Task<IReadOnlyList<StockLevel>> GetStockBatchAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

// Inventory could not give a trustworthy answer in time: it is down, too slow, or the circuit
// breaker is open after repeated failures. The stock check falls back to the stale cache.
public class InventoryUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
