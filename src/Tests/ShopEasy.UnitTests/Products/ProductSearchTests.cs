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
using ShopEasy.ProductService.Models;
using ShopEasy.Shared.Services;
using ShopEasy.UnitTests.Infrastructure;
using StackExchange.Redis;
using Xunit;

namespace ShopEasy.UnitTests.Products;

// GET /api/v1/products?search=... : the storefront's search box. Public, so what the caller types
// is limited, matched literally, and never becomes a key in Redis.
public class ProductSearchTests
{
    private readonly IDistributedCache _redis = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static Product AProduct(string name, string description, string category, bool active = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Description = description, Price = 10, Category = category, IsActive = active,
        CreatedBy = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static ProductDbContext InMemoryDatabase()
    {
        var options = new DbContextOptionsBuilder<ProductDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ProductDbContext(options);
        db.Products.AddRange(
            AProduct("Headphones", "Wireless, noise cancelling", "Electronics"),
            AProduct("Webcam", "Full HD, works with any headphones jack", "Electronics"),
            AProduct("Novel", "A story about headphones", "Books"),
            AProduct("Old Headphones", "No longer sold", "Electronics", active: false));
        db.SaveChanges();
        return db;
    }

    private ProductsController ControllerFor(ProductDbContext db) =>
        new(db, new CacheService(_redis, NullLogger<CacheService>.Instance),
            Substitute.For<IConnectionMultiplexer>(), NullLogger<ProductsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private async Task<(PagedResult<ProductResponseV1> Page, string? Cache)> SearchAsync(
        ProductDbContext db, string? search, string? category = null, int page = 1, int pageSize = 10)
    {
        var controller = ControllerFor(db);
        var result = await controller.GetAll(category, page, pageSize, search);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((PagedResult<ProductResponseV1>)ok.Value!, controller.Response.Headers["X-Cache"]);
    }

    private static string[] Names(PagedResult<ProductResponseV1> page) => page.Items.Select(p => p.Name).Order().ToArray();

    [Theory]
    [InlineData("webcam")]
    [InlineData("WEBCAM")]
    [InlineData("  Webcam  ")]
    public async Task ASearch_FindsTheNameInAnyLetterCase_IgnoringSurroundingSpaces(string search)
    {
        var (page, _) = await SearchAsync(InMemoryDatabase(), search);

        Names(page).Should().Equal("Webcam");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ASearch_AlsoLooksInTheDescription_AndNeverFindsAProductThatIsNotSold()
    {
        var (page, _) = await SearchAsync(InMemoryDatabase(), "headphones");

        Names(page).Should().Equal("Headphones", "Novel", "Webcam");
    }

    [Fact]
    public async Task ASearch_CanBeNarrowedToACategory_AndPaged()
    {
        var db = InMemoryDatabase();

        var (electronics, _) = await SearchAsync(db, "headphones", category: "Electronics");
        var (firstOfTwo, _) = await SearchAsync(db, "headphones", page: 1, pageSize: 2);
        var (secondOfTwo, _) = await SearchAsync(db, "headphones", page: 2, pageSize: 2);

        Names(electronics).Should().Equal("Headphones", "Webcam");
        firstOfTwo.TotalCount.Should().Be(3);
        firstOfTwo.Items.Should().HaveCount(2);
        secondOfTwo.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ASearchThatFindsNothing_IsAnEmptyPage_NotAnError()
    {
        var (page, _) = await SearchAsync(InMemoryDatabase(), "zebra");

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ASearch_IsNeverCached_AndNeverAnsweredFromTheCacheOfTheWholeList()
    {
        var db = InMemoryDatabase();
        (await SearchAsync(db, null)).Cache.Should().Be("MISS");
        (await SearchAsync(db, null)).Cache.Should().Be("HIT"); // the whole list is cached

        var (first, firstCache) = await SearchAsync(db, "novel");
        var (second, secondCache) = await SearchAsync(db, "novel");

        Names(first).Should().Equal("Novel");
        Names(second).Should().Equal("Novel");
        (firstCache, secondCache).Should().Be(("MISS", "MISS"));
        (await _redis.GetStringAsync("products:all:all:1:10:novel")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankSearch_IsTheWholeList_AndSharesItsCacheKey(string blank)
    {
        var db = InMemoryDatabase();
        (await SearchAsync(db, null)).Cache.Should().Be("MISS");

        var (page, cache) = await SearchAsync(db, blank);

        cache.Should().Be("HIT");
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task ASearchTextThatIsTooLong_IsRefused()
    {
        var tooLong = new string('a', ProductsController.MaxSearchLength + 1);

        var result = await ControllerFor(InMemoryDatabase()).GetAll(null, 1, 10, tooLong);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ASearchTextAtTheLimit_IsAccepted()
    {
        var (page, _) = await SearchAsync(InMemoryDatabase(), new string('a', ProductsController.MaxSearchLength));

        page.Items.Should().BeEmpty();
    }

    // The InMemory provider compares strings in memory; what the database does with % and _ (the
    // wildcards of LIKE) can only be seen on PostgreSQL.
    [RequiresPostgresFact]
    public async Task OnPostgreSql_PercentAndUnderscore_AreLookedForAsCharacters()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("productsearch");
        var options = new DbContextOptionsBuilder<ProductDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new ProductDbContext(options);
        await db.Database.MigrateAsync();
        db.Products.AddRange(
            AProduct("Mug 50% off", "Ceramic", "Kitchen"),
            AProduct("Mug a_b", "Ceramic", "Kitchen"),
            AProduct("Mug axb", "Ceramic", "Kitchen"));
        await db.SaveChangesAsync();

        var (percent, _) = await SearchAsync(db, "%");
        var (underscore, _) = await SearchAsync(db, "a_b");
        var (mixedCase, _) = await SearchAsync(db, "MUG AXB");

        Names(percent).Should().Equal("Mug 50% off");
        Names(underscore).Should().Equal("Mug a_b");
        Names(mixedCase).Should().Equal("Mug axb");
    }
}
