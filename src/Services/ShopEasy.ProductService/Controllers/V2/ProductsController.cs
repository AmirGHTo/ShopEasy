using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.ProductService.Data;
using ShopEasy.ProductService.Models;

namespace ShopEasy.ProductService.Controllers.V2;

[ApiController]
[ApiVersion("2.0")]
[Route("api/v{version:apiVersion}/products")]
public class ProductsController(
    ProductDbContext db,
    ILogger<ProductsController> logger) : ControllerBase
{
    private readonly ProductDbContext _db = db;
    private readonly ILogger<ProductsController> _logger = logger;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var products = await _db.Products
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return Ok(products.Select(MapToV2));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var p = await _db.Products.FindAsync(id);
        if (p is null) return NotFound();

        return Ok(MapToV2(p));
    }

    private static ProductResponseV2 MapToV2(Product p) => new(
        Id: p.Id,
        Name: p.Name,
        Description: p.Description,
        Price: p.Price,
        PriceFormatted: p.Price.ToString("C", System.Globalization.CultureInfo
                            .GetCultureInfo("en-US")),
        Currency: "USD",
        Category: p.Category,
        ImageUrl: p.ImageUrl,
        IsActive: p.IsActive,
        CreatedAt: p.CreatedAt,
        CreatedBy: p.CreatedBy
    );
}