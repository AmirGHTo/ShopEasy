using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Orders;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// A request refused before anything is saved is counted once, with the reason the caller was given.
[Collection(MetricsCollection.Name)]
public class RejectedOrderMetricsTests
{
    private static readonly Guid Desk = OrderPlacementHarness.Desk;

    // Places one order against a shop arranged to refuse it; the status code and how much the reason's count grew
    private static async Task<(int Status, double Counted)> Refused(
        string reason, Action<OrderPlacementHarness> arrange, int quantity = 1, string? key = null)
    {
        using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var shop = new OrderPlacementHarness();
        arrange(shop);
        var series = $"shopeasy_orders_rejected_total{{reason=\"{reason}\"}}";
        var before = await MetricValues.ReadAsync(series);

        var outcome = await shop.PlaceAsync(db, OrderPlacementHarness.Order(Desk, quantity), key);

        return (outcome.StatusCode, await MetricValues.ReadAsync(series) - before);
    }

    [Fact]
    public async Task ARequestThatIsNotAValidOrder_IsCounted() =>
        (await Refused("invalid_request", _ => { }, quantity: 0)).Should().Be((400, 1));

    [Fact]
    public async Task AnIdempotencyKeyThatIsNotAllowed_IsCountedAsAnInvalidRequest() =>
        (await Refused("invalid_request", _ => { }, key: "not a valid key")).Should().Be((400, 1));

    [Fact]
    public async Task AProductTheCatalogDoesNotKnow_IsCounted() =>
        (await Refused("unknown_product", shop => shop.Catalog.GetProductsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<Guid, CatalogProduct?>>(
                call.Arg<IEnumerable<Guid>>().ToDictionary(id => id, _ => (CatalogProduct?)null)))))
        .Should().Be((400, 1));

    [Fact]
    public async Task ACatalogThatCannotAnswer_IsCounted() =>
        (await Refused("catalog_unavailable", shop => shop.Catalog.GetProductsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<Guid, CatalogProduct?>>(_ => throw new ProductCatalogUnavailableException("down"))))
        .Should().Be((503, 1));

    [Fact]
    public async Task MoreThanTheStockHolds_IsCounted() =>
        (await Refused("insufficient_stock", shop => shop.Available = 1, quantity: 5)).Should().Be((400, 1));

    [Fact]
    public async Task StockThatCannotBeRead_IsCounted() =>
        (await Refused("inventory_unreachable", shop => shop.Stock.GetStockBatchAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<StockInfo>()))))
        .Should().Be((400, 1));
}
