using FluentAssertions;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// What must be true of a settled order given what Payment holds for it.
public class ConsistencyRulesTests
{
    private static CheckedOrder Order(OrderStatus status, decimal total = 100m) =>
        new(Guid.NewGuid(), status, total, DateTime.UtcNow);

    private static PaymentRow Payment(string status, decimal amount = 100m) => new(Guid.NewGuid(), status, amount);

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    public void APaidOrder_WithOneSuccessfulPaymentForItsTotal_BreaksNothing(OrderStatus status) =>
        ConsistencyRules.Broken(Order(status), [Payment("Succeeded")]).Should().BeEmpty();

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Refunded")]
    public void AConfirmedOrder_WithoutASuccessfulPayment_IsGoodsForNothing(string paymentStatus) =>
        ConsistencyRules.Broken(Order(OrderStatus.Confirmed), [Payment(paymentStatus)])
            .Should().Equal(ConsistencyRules.ConfirmedNotPaid);

    [Fact]
    public void AConfirmedOrder_WithNoPaymentAtAll_IsGoodsForNothing() =>
        ConsistencyRules.Broken(Order(OrderStatus.Confirmed), []).Should().Equal(ConsistencyRules.ConfirmedNotPaid);

    [Fact]
    public void AConfirmedOrder_ChargedAnotherAmount_IsFlagged() =>
        ConsistencyRules.Broken(Order(OrderStatus.Confirmed, total: 100m), [Payment("Succeeded", amount: 99.99m)])
            .Should().Equal(ConsistencyRules.AmountDiffers);

    [Fact]
    public void ACustomerChargedTwice_IsFlagged_AndSoIsTheDifferenceInAmount()
    {
        var broken = ConsistencyRules.Broken(Order(OrderStatus.Confirmed), [Payment("Succeeded"), Payment("Succeeded")]);

        broken.Should().Equal(ConsistencyRules.PaidTwice);
    }

    [Fact]
    public void ASuccessfulPaymentNextToARefundedOne_IsNotATwiceCharge() =>
        ConsistencyRules.Broken(Order(OrderStatus.Confirmed), [Payment("Refunded"), Payment("Succeeded")]).Should().BeEmpty();

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PaymentFailed)]
    public void ACancelledOrder_WhoseCustomerIsStillCharged_KeepsTheMoney(OrderStatus status) =>
        ConsistencyRules.Broken(Order(status), [Payment("Succeeded")]).Should().Equal(ConsistencyRules.CancelledStillCharged);

    [Theory]
    [InlineData("Refunded")]
    [InlineData("Failed")]
    [InlineData("Pending")]
    public void ACancelledOrder_RefundedOrNeverCharged_IsFine(string paymentStatus) =>
        ConsistencyRules.Broken(Order(OrderStatus.Cancelled), [Payment(paymentStatus)]).Should().BeEmpty();

    [Fact]
    public void ACancelledOrder_WithNoPayment_IsFine() =>
        ConsistencyRules.Broken(Order(OrderStatus.Cancelled), []).Should().BeEmpty();

    [Fact]
    public void EveryRuleHasAName_AndEveryNameIsListed()
    {
        var constants = typeof(ConsistencyRules).GetFields().Select(f => (string)f.GetRawConstantValue()!);

        ConsistencyRules.All.Should().BeEquivalentTo(constants, "the metric has a series for each of them");
    }

    [Fact]
    public void AnOrderStillBeingRefundedOrReleased_IsNotJudgedYet() =>
        ConsistencyRules.Compensating.Should().BeEquivalentTo(
            [SagaStatus.ReleasingInventory, SagaStatus.RefundingPayment, SagaStatus.RestockingInventory]);
}
