namespace ShopEasy.OrderService.Services;

// A product's stock level as the order flow sees it (from the cache, or from Inventory when the
// cache has nothing). IsStale marks an answer taken from the long-lived copy because Inventory
// could not be asked.
public record StockInfo(
    Guid ProductId,
    string ProductName,
    int AvailableQuantity,
    bool IsLowStock,
    int Quantity = 0,
    int ReservedQty = 0,
    bool IsStale = false,
    DateTime CachedAt = default);
