namespace ShopEasy.EndToEndTests.Support;

// The products the catalog starts with, and what the tests need to know about the stock of one.
// Each scenario uses a product of its own, so that the stock it reads is changed by nobody else.
public static class Catalog
{
    public static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly Guid Keyboard = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid Chair = Guid.Parse("00000000-0000-0000-0000-000000000004");

    public static async Task<decimal> PriceAsync(Shopper shopper, Guid productId)
    {
        var product = await shopper.GetAsync($"/api/v1/products/{productId}");
        product.Status.Should().Be(HttpStatusCode.OK, "the product is in the catalog: {0}", product);
        return product.Json.Number("price");
    }

    public static async Task<Stock> StockAsync(Shopper shopper, Guid productId)
    {
        var item = await shopper.GetAsync($"/api/inventory/{productId}");
        item.Status.Should().Be(HttpStatusCode.OK, "the product has stock: {0}", item);
        return new Stock((int)item.Json.Number("quantity"), (int)item.Json.Number("availableQuantity"));
    }
}

// Units on the shelf, and units on the shelf that no order in flight has already claimed
public readonly record struct Stock(int Quantity, int Available);
