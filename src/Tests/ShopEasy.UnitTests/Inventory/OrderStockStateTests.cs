using FluentAssertions;
using ShopEasy.InventoryService.Data;
using Xunit;

namespace ShopEasy.UnitTests.Inventory;

// What the ledger says about one order's stock, including the stock put back after a cancellation.
public class OrderStockStateTests
{
    private static readonly Guid ProductA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProductB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static StockTransaction Row(Guid orderId, Guid productId, string reason, int change) => new()
    {
        Id = Guid.NewGuid(), ProductId = productId, OrderId = orderId, Reason = reason, QuantityChange = change, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public void AnOrderIsFullyRestocked_OnlyWhenEveryReservedProductWasPutBack()
    {
        var orderId = Guid.NewGuid();
        var rows = new List<StockTransaction>
        {
            Row(orderId, ProductA, StockReasons.Reserved(orderId), -2), Row(orderId, ProductA, StockReasons.Committed(orderId), -2),
            Row(orderId, ProductB, StockReasons.Reserved(orderId), -1), Row(orderId, ProductB, StockReasons.Committed(orderId), -1),
            Row(orderId, ProductA, StockReasons.Restocked(orderId), 2),
        };

        var half = OrderStockState.From(orderId, rows);
        rows.Add(Row(orderId, ProductB, StockReasons.Restocked(orderId), 1));
        var all = OrderStockState.From(orderId, rows);

        (half.AnyRestocked, half.FullyRestocked).Should().Be((true, false));
        (all.AnyRestocked, all.FullyRestocked).Should().Be((true, true));
    }

    [Fact]
    public void AnotherOrdersRestock_IsNotCounted()
    {
        var ours = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var rows = new[]
        {
            Row(ours, ProductA, StockReasons.Reserved(ours), -2),
            Row(theirs, ProductA, StockReasons.Restocked(theirs), 2),
        };

        OrderStockState.From(ours, rows).AnyRestocked.Should().BeFalse();
    }
}
