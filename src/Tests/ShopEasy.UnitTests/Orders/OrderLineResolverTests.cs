using FluentAssertions;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

public class OrderLineResolverTests
{
    private static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static IReadOnlyDictionary<Guid, CatalogProduct?> Catalog(
        params (Guid Id, CatalogProduct? Product)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Product);

    [Fact]
    public void UsesPriceAndNameFromTheCatalog()
    {
        var catalog = Catalog((Webcam, new CatalogProduct(Webcam, "4K Webcam", 89.99m, true)));

        var result = OrderLineResolver.Resolve([new RequestedLine(Webcam, 3)], catalog);

        result.IsValid.Should().BeTrue();
        result.Lines.Should().ContainSingle().Which.Should().Be(
            new ResolvedLine(Webcam, "4K Webcam", 3, 89.99m));
    }

    [Fact]
    public void UnknownProduct_IsReportedAsMissing()
    {
        var catalog = Catalog((Webcam, null));

        var result = OrderLineResolver.Resolve([new RequestedLine(Webcam, 1)], catalog);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("does not exist");
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ProductMissingFromCatalogResponse_IsReportedAsMissing()
    {
        var result = OrderLineResolver.Resolve([new RequestedLine(Webcam, 1)], Catalog());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("does not exist");
    }

    [Fact]
    public void InactiveProduct_IsRejected()
    {
        var catalog = Catalog((Desk, new CatalogProduct(Desk, "Standing Desk", 599.99m, false)));

        var result = OrderLineResolver.Resolve([new RequestedLine(Desk, 1)], catalog);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("no longer available");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ProductWithoutValidPrice_IsRejected(double price)
    {
        var catalog = Catalog((Desk, new CatalogProduct(Desk, "Standing Desk", (decimal)price, true)));

        var result = OrderLineResolver.Resolve([new RequestedLine(Desk, 1)], catalog);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("no valid price");
    }

    [Fact]
    public void MixedLines_ReportEveryProblem_AndNoOrderableLinesWhenInvalid()
    {
        var catalog = Catalog(
            (Webcam, new CatalogProduct(Webcam, "4K Webcam", 89.99m, true)),
            (Desk, new CatalogProduct(Desk, "Standing Desk", 599.99m, false)));
        var unknown = Guid.NewGuid();

        var result = OrderLineResolver.Resolve(
            [new RequestedLine(Webcam, 1), new RequestedLine(Desk, 1), new RequestedLine(unknown, 1)],
            catalog);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Lines.Should().ContainSingle().Which.ProductId.Should().Be(Webcam);
    }
}
