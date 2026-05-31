namespace ShopEasy.ProductService.Models;

public record ProductResponse(
    Guid    Id,
    string  Name,
    string  Description,
    decimal Price,
    string  Category,
    string  ImageUrl,
    bool    IsActive,
    DateTime CreatedAt);

public record CreateProductRequest(
    string  Name,
    string  Description,
    decimal Price,
    string  Category,
    string? ImageUrl);

public record UpdateProductRequest(
    string?  Name,
    string?  Description,
    decimal? Price,
    string?  Category,
    string?  ImageUrl,
    bool?    IsActive);

public record PagedResult<T>(
    IEnumerable<T> Items,
    int TotalCount,
    int Page,
    int PageSize);
