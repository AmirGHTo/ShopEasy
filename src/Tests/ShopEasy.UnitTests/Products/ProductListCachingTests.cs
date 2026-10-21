using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.ProductService.Controllers;
using ShopEasy.ProductService.Data;
using ShopEasy.Shared.Services;
using StackExchange.Redis;
using Xunit;

namespace ShopEasy.UnitTests.Products;

// The product list is public, and its cache key holds the category and the page of the request.
// Only pages that have products may be cached, and a blank category is the whole list: otherwise
// anyone could fill Redis with keys for categories and pages that do not exist.
public class ProductListCachingTests
{
    private readonly IDistributedCache _redis = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly ProductDbContext _db = Database();

    private static ProductDbContext Database()
    {
        var options = new DbContextOptionsBuilder<ProductDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ProductDbContext(options);
        foreach (var (name, category) in new[] { ("Headphones", "Electronics"), ("Webcam", "Electronics"), ("Novel", "Books") })
            db.Products.Add(new Product
            {
                Id = Guid.NewGuid(), Name = name, Description = name, Price = 10, Category = category,
                CreatedBy = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        db.SaveChanges();
        return db;
    }

    // one request: the X-Cache header of its answer
    private async Task<string?> RequestAsync(string? category, int page = 1)
    {
        var controller = new ProductsController(_db, new CacheService(_redis, NullLogger<CacheService>.Instance),
            Substitute.For<IConnectionMultiplexer>(), NullLogger<ProductsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        await controller.GetAll(category, page, pageSize: 10);
        return controller.Response.Headers["X-Cache"];
    }

    [Fact]
    public async Task APageWithProducts_IsCached_ForTheNextRequest()
    {
        (await RequestAsync("Electronics")).Should().Be("MISS");
        (await RequestAsync("Electronics")).Should().Be("HIT");
        (await RequestAsync(null)).Should().Be("MISS");
        (await RequestAsync(null)).Should().Be("HIT");
    }

    [Fact]
    public async Task ACategoryThatDoesNotExist_IsNeverCached()
    {
        (await RequestAsync("Nonexistent")).Should().Be("MISS");
        (await RequestAsync("Nonexistent")).Should().Be("MISS");
        (await _redis.GetStringAsync("products:all:Nonexistent:1:10")).Should().BeNull();
    }

    [Fact]
    public async Task APagePastTheEnd_IsNeverCached()
    {
        (await RequestAsync("Electronics", page: 2)).Should().Be("MISS");
        (await RequestAsync("Electronics", page: 2)).Should().Be("MISS");
        (await _redis.GetStringAsync("products:all:Electronics:2:10")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankCategory_IsTheWholeList_AndSharesItsCacheKey(string blank)
    {
        (await RequestAsync(null)).Should().Be("MISS");

        (await RequestAsync(blank)).Should().Be("HIT");
        (await _redis.GetStringAsync($"products:all:{blank}:1:10")).Should().BeNull("a blank category has no key of its own");
    }
}
