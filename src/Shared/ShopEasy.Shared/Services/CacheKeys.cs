namespace ShopEasy.Shared.Services;

// Centralized cache key definitions.
// Using constants prevents typos and makes invalidation reliable.
public static class CacheKeys
{
    // Product cache keys
    public const string ProductsAll = "products:all";
    public const string ProductsCategories = "products:categories";
    public static string Product(Guid id) => $"products:{id}";
    public const string ProductsPrefix = "products:";

    // Inventory cache keys
    public static string Inventory(Guid productId) => $"inventory:{productId}";
    public const string InventoryAll = "inventory:all";
    public const string InventoryPrefix = "inventory:";

}