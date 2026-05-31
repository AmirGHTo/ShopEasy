namespace ShopEasy.ProductService.Models;

// v1 response
public record ProductResponseV1(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    string Category,
    string ImageUrl,
    bool IsActive
);

// v2 response
public record ProductResponseV2(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    string PriceFormatted,
    string Currency,
    string Category,
    string ImageUrl,
    bool IsActive,
    DateTime CreatedAt,
    string CreatedBy
);