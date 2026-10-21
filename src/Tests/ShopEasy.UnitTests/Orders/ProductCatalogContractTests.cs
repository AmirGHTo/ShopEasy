using Asp.Versioning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Services;
using ShopEasy.ProductService.Data;
using ShopEasy.Shared.Security;
using ShopEasy.Shared.Services;
using ShopEasy.UnitTests.Support;
using StackExchange.Redis;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// What the Order service reads from the Product API, with both real halves: the Order service's
// ProductCatalogClient (which has its own copy of the product shape, CatalogProduct) talking over
// HTTP to the real Product controllers (ProductResponseV1). The client tests next door answer with
// hand-written JSON; this is what notices when the two stop agreeing: a renamed property, a changed
// route or version, a different type for the price.
public sealed class ProductCatalogContractTests : IAsyncLifetime
{
    private static readonly Guid Headphones = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Retired = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Unknown = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private AuthHost _productApi = default!;
    private ProductCatalogClient _orderClient = default!;

    public async Task InitializeAsync()
    {
        var database = Guid.NewGuid().ToString();

        // the Product API as the service starts it (versioning, controllers), with an in-memory database
        _productApi = await AuthHost.StartAsync(
            ApiAudiences.Product, AccessPolicies.Product, typeof(ShopEasy.ProductService.Controllers.ProductsController).Assembly,
            configure: builder =>
            {
                builder.Services.AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ApiVersionReader = ApiVersionReader.Combine(new UrlSegmentApiVersionReader(), new HeaderApiVersionReader("X-Api-Version"));
                }).AddMvc();
                builder.Services.AddDbContext<ProductDbContext>(o => o.UseInMemoryDatabase(database));
                builder.Services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
                builder.Services.AddScoped<CacheService>();
                builder.Services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
            });

        await using (var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseInMemoryDatabase(database).Options))
        {
            db.Products.AddRange(Product(Headphones, "Wireless Headphones", 299.99m, active: true), Product(Retired, "Old Model", 10.5m, active: false));
            await db.SaveChangesAsync();
        }

        _orderClient = new ProductCatalogClient(_productApi.Client, NullLogger<ProductCatalogClient>.Instance);
    }

    public Task DisposeAsync() => _productApi.DisposeAsync().AsTask();

    private static Product Product(Guid id, string name, decimal price, bool active) => new()
    {
        Id = id, Name = name, Description = "d", Price = price, Category = "c", IsActive = active,
        CreatedBy = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task TheOrderService_ReadsAProduct_AsTheProductServiceSendsIt()
    {
        var found = await _orderClient.GetProductsAsync([Headphones]);

        found[Headphones].Should().Be(new CatalogProduct(Headphones, "Wireless Headphones", 299.99m, true));
    }

    [Fact]
    public async Task AProductThatIsNoLongerOffered_ArrivesAsInactive()
    {
        var found = await _orderClient.GetProductsAsync([Retired]);

        found[Retired].Should().Be(new CatalogProduct(Retired, "Old Model", 10.5m, false));
    }

    [Fact]
    public async Task AProductThatDoesNotExist_IsAnswered404_AndReadAsMissing()
    {
        var found = await _orderClient.GetProductsAsync([Unknown]);

        found.Should().ContainKey(Unknown).WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task SeveralProducts_AreEachAnswered()
    {
        var found = await _orderClient.GetProductsAsync([Headphones, Retired, Unknown]);

        found.Keys.Should().BeEquivalentTo([Headphones, Retired, Unknown]);
        found[Headphones]!.Price.Should().Be(299.99m);
        found[Retired]!.IsActive.Should().BeFalse();
        found[Unknown].Should().BeNull();
    }
}
