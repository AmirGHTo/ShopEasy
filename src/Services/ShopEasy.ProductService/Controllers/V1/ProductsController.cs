using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.ProductService.Data;
using ShopEasy.ProductService.Models;
using ShopEasy.Shared.Services;
using StackExchange.Redis;
using ShopEasy.Shared.Security;

namespace ShopEasy.ProductService.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Produces("application/json")]
public class ProductsController(
    ProductDbContext db,
    CacheService cache,
    IConnectionMultiplexer redis,
    ILogger<ProductsController> logger) : ControllerBase
{
    private readonly ProductDbContext _db = db;
    private readonly CacheService _cache = cache;
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILogger<ProductsController> _logger = logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // ── GET /api/v1/products ──────────────────────────────────────────────────
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<PagedResult<ProductResponseV1>>> GetAll(
        [FromQuery] string? category,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        // A blank category is no filter: it must not get a cache key of its own
        if (string.IsNullOrWhiteSpace(category)) category = null;

        // Build a cache key that includes filter parameters
        var cacheKey = $"{CacheKeys.ProductsAll}:{category ?? "all"}:{page}:{pageSize}";

        // Try cache first
        var cached = await _cache.GetAsync<PagedResult<ProductResponseV1>>(cacheKey);
        if (cached is not null)
        {
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        // Cache miss — query database
        var query = _db.Products
            .Where(p => p.IsActive)
            .AsQueryable();

        if (category is not null)
            query = query.Where(p => p.Category == category);

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductResponseV1(
                p.Id, p.Name, p.Description, p.Price,
                p.Category, p.ImageUrl, p.IsActive))
            .ToListAsync();

        var result = new PagedResult<ProductResponseV1>(items, total, page, pageSize);

        // Store in cache, but only a page that has products. The key holds the category and the page
        // of an anonymous request: caching the empty answer for a category or page that does not exist
        // would let anyone fill Redis with keys that are worth nothing.
        if (items.Count > 0)
            await _cache.SetAsync(cacheKey, result, CacheTtl);
        Response.Headers["X-Cache"] = "MISS";

        return Ok(result);
    }

    // ── GET /api/v1/products/{id} ─────────────────────────────────────────────
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ProductResponseV1>> GetById(Guid id)
    {
        var cacheKey = CacheKeys.Product(id);

        var cached = await _cache.GetAsync<ProductResponseV1>(cacheKey);
        if (cached is not null)
        {
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        var product = await _db.Products.FindAsync(id);
        if (product is null) return NotFound();

        var result = new ProductResponseV1(
            product.Id, product.Name, product.Description, product.Price,
            product.Category, product.ImageUrl, product.IsActive);

        await _cache.SetAsync(cacheKey, result, CacheTtl);
        Response.Headers["X-Cache"] = "MISS";

        return Ok(result);
    }

    // ── POST /api/v1/products ─────────────────────────────────────────────────
    [HttpPost]
    [Authorize(Policy = PolicyNames.ProductsWrite)]
    public async Task<ActionResult<ProductResponseV1>> Create(
        [FromBody] CreateProductRequest request)
    {
        var userId = User.FindFirst("sub")?.Value ?? "unknown";
        logger.LogInformation("Admin {UserId} creating product: {Name}", userId, request.Name);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Price = request.Price,
            Category = request.Category,
            ImageUrl = request.ImageUrl ?? string.Empty,
            IsActive = true,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        // Invalidate all product list caches — new product must appear in lists
        await _cache.InvalidateByPrefixAsync(CacheKeys.ProductsPrefix, _redis);
        logger.LogInformation("Cache invalidated after product {ProductId} created", product.Id);

        var response = new ProductResponseV1(
            product.Id, product.Name, product.Description, product.Price,
            product.Category, product.ImageUrl, product.IsActive);

        return CreatedAtAction(nameof(GetById), new { id = product.Id }, response);
    }

    // ── PUT /api/v1/products/{id} ─────────────────────────────────────────────
    [HttpPut("{id:guid}")]
    [Authorize(Policy = PolicyNames.ProductsWrite)]
    public async Task<ActionResult<ProductResponseV1>> Update(
        Guid id, [FromBody] UpdateProductRequest request)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return NotFound();

        if (request.Name is not null) product.Name = request.Name;
        if (request.Description is not null) product.Description = request.Description;
        if (request.Price is not null) product.Price = request.Price.Value;
        if (request.Category is not null) product.Category = request.Category;
        if (request.ImageUrl is not null) product.ImageUrl = request.ImageUrl;
        if (request.IsActive is not null) product.IsActive = request.IsActive.Value;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Invalidate this product and all list caches
        await _cache.InvalidateAsync(CacheKeys.Product(id));
        await _cache.InvalidateByPrefixAsync(CacheKeys.ProductsPrefix, _redis);
        logger.LogInformation("Cache invalidated after product {ProductId} updated", id);

        return Ok(new ProductResponseV1(
            product.Id, product.Name, product.Description, product.Price,
            product.Category, product.ImageUrl, product.IsActive));
    }

    // ── DELETE /api/v1/products/{id} ──────────────────────────────────────────
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PolicyNames.ProductsWrite)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return NotFound();

        product.IsActive = false;
        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Invalidate all product caches
        await _cache.InvalidateAsync(CacheKeys.Product(id));
        await _cache.InvalidateByPrefixAsync(CacheKeys.ProductsPrefix, _redis);
        logger.LogInformation("Cache invalidated after product {ProductId} deleted", id);

        return NoContent();
    }

    // ── GET /api/v1/products/categories ──────────────────────────────────────
    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<string>>> GetCategories()
    {
        var cached = await _cache.GetAsync<List<string>>(CacheKeys.ProductsCategories);
        if (cached is not null)
        {
            Response.Headers["X-Cache"] = "HIT";
            return Ok(cached);
        }

        var categories = await _db.Products
            .Where(p => p.IsActive)
            .Select(p => p.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        await _cache.SetAsync(CacheKeys.ProductsCategories, categories, CacheTtl);
        Response.Headers["X-Cache"] = "MISS";

        return Ok(categories);
    }
}