using FluentAssertions;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

public class OrderRequestValidatorTests
{
    private static readonly Guid ProductA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProductB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static PlaceOrderRequest Request(params OrderItemRequest?[] items) =>
        new() { Items = items.ToList() };

    private static OrderItemRequest Item(Guid productId, int quantity) =>
        new() { ProductId = productId, Quantity = quantity };

    [Fact]
    public void NullRequest_IsRejected()
    {
        var result = OrderRequestValidator.Validate(null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("At least one item");
    }

    [Fact]
    public void MissingItemsList_IsRejected()
    {
        var result = OrderRequestValidator.Validate(new PlaceOrderRequest { Items = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("At least one item");
    }

    [Fact]
    public void EmptyItemsList_IsRejected()
    {
        var result = OrderRequestValidator.Validate(Request());

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidRequest_ReturnsLines()
    {
        var result = OrderRequestValidator.Validate(Request(Item(ProductA, 2), Item(ProductB, 1)));

        result.IsValid.Should().BeTrue();
        result.Lines.Should().BeEquivalentTo(new[]
        {
            new RequestedLine(ProductA, 2),
            new RequestedLine(ProductB, 1)
        });
    }

    [Fact]
    public void ClientSuppliedPriceAndName_AreNotPartOfTheValidatedLines()
    {
        var item = new OrderItemRequest
        {
            ProductId = ProductA,
            Quantity = 1,
            ProductName = "Totally Free Item",
            UnitPrice = 0.01m
        };

        var result = OrderRequestValidator.Validate(Request(item));

        result.IsValid.Should().BeTrue();
        // RequestedLine only carries ProductId + Quantity — price/name can never leak through.
        result.Lines.Should().ContainSingle().Which.Should().Be(new RequestedLine(ProductA, 1));
    }

    [Fact]
    public void DuplicateProducts_AreMergedIntoOneLine()
    {
        var result = OrderRequestValidator.Validate(
            Request(Item(ProductA, 2), Item(ProductB, 1), Item(ProductA, 3)));

        result.IsValid.Should().BeTrue();
        result.Lines.Should().BeEquivalentTo(new[]
        {
            new RequestedLine(ProductA, 5),
            new RequestedLine(ProductB, 1)
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void QuantityBelowOne_IsRejected(int quantity)
    {
        var result = OrderRequestValidator.Validate(Request(Item(ProductA, quantity)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("quantity must be at least 1");
    }

    [Fact]
    public void QuantityAboveMaximum_IsRejected()
    {
        var result = OrderRequestValidator.Validate(
            Request(Item(ProductA, OrderLimits.MaxQuantityPerProduct + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("must not exceed");
    }

    [Fact]
    public void QuantityAtMaximum_IsAccepted()
    {
        var result = OrderRequestValidator.Validate(
            Request(Item(ProductA, OrderLimits.MaxQuantityPerProduct)));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void MergedQuantityAboveMaximum_IsRejected()
    {
        // each line is fine on its own, but together they exceed the per-product limit
        var half = OrderLimits.MaxQuantityPerProduct / 2 + 1;

        var result = OrderRequestValidator.Validate(Request(Item(ProductA, half), Item(ProductA, half)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("total quantity");
    }

    [Fact]
    public void EmptyProductId_IsRejected()
    {
        var result = OrderRequestValidator.Validate(Request(Item(Guid.Empty, 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("productId is required");
    }

    [Fact]
    public void NullItem_IsRejected()
    {
        var result = OrderRequestValidator.Validate(Request(Item(ProductA, 1), null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("Item 2: must not be null");
    }

    [Fact]
    public void TooManyItems_IsRejected()
    {
        var items = Enumerable.Range(1, OrderLimits.MaxItemsPerOrder + 1)
            .Select(i => Item(Guid.NewGuid(), 1))
            .ToArray();

        var result = OrderRequestValidator.Validate(Request(items));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("at most"));
    }

    [Fact]
    public void NotesTooLong_IsRejected()
    {
        var request = new PlaceOrderRequest
        {
            Items = [Item(ProductA, 1)],
            Notes = new string('x', OrderLimits.MaxNotesLength + 1)
        };

        var result = OrderRequestValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("Notes");
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var result = OrderRequestValidator.Validate(
            Request(Item(Guid.Empty, 0), Item(ProductA, OrderLimits.MaxQuantityPerProduct + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Count.Should().BeGreaterThanOrEqualTo(3);
    }
}
