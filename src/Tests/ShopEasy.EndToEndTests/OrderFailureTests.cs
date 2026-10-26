namespace ShopEasy.EndToEndTests;

// The paths of an order that do not end in a sale: a payment the provider declines, and a customer
// who cancels while the saga is still working. Both must leave the shop as it was: the stock back on
// the shelf, no money kept, no order half done.
//
// These run as the second customer of the run. Its order budget (10 requests a minute) is nearly
// unused, and the tests of the collection run one after the other, so their readings of the stock
// are not disturbed by other orders.
[Collection(ShopCollection.Name)]
public class OrderFailureTests(ShopFixture shop)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    // Long enough for a payment command that was still on its way when the order was cancelled to
    // arrive, be refused or refunded, and for the stock to come back
    private static readonly TimeSpan LateMessages = TimeSpan.FromSeconds(5);

    private Shopper Customer => shop.Neighbour;

    [Fact]
    public async Task APaymentTheProviderDeclines_CancelsTheOrder_PutsTheStockBack_AndChargesNothing()
    {
        // The provider declines any single payment above 10,000, whatever Compose forces for the random
        // bank decline: 34 headphones at 299.99 are 10,199.66
        const int quantity = 34;
        var before = await Catalog.StockAsync(Customer, Catalog.Headphones);

        var placed = await Customer.PostAsync("/api/orders",
            new { items = new[] { new { productId = Catalog.Headphones, quantity } } }, Guid.NewGuid().ToString());
        placed.Status.Should().Be(HttpStatusCode.Created, "{0}", placed);
        placed.Json.Number("totalAmount").Should().BeGreaterThan(10_000m, "the order has to be above the limit");
        var orderId = placed.Json.Text("id");

        // the saga reserves the stock, asks for the payment, is told no, releases the stock, and cancels
        var order = await Wait.UntilAsync(
            () => Customer.GetAsync($"/api/orders/{orderId}"),
            reply => reply.Status == HttpStatusCode.OK && reply.Json.Text("statusName") != "Pending",
            $"order {orderId} to be decided", Settle);
        order.Json.Text("statusName").Should().Be("Cancelled", "a declined payment cancels the order: {0}", order);
        order.Json.Text("failureReason").Should().Contain("single transaction limit");

        // nothing was charged: the provider's answer is recorded as a failure, never as a success
        var payment = await Customer.GetAsync($"/api/payments/order/{orderId}");
        payment.Status.Should().Be(HttpStatusCode.OK, "{0}", payment);
        payment.Json.Prop("status").ToString().Should().BeOneOf("Failed", "2");

        // and the stock reserved for the order is back
        var after = await Wait.UntilAsync(
            () => Catalog.StockAsync(Customer, Catalog.Headphones),
            stock => stock == before, "the reserved stock to be released", TimeSpan.FromSeconds(45));
        after.Should().Be(before);
    }

    // A customer cancels at different moments. The saga only starts when the Order service's outbox
    // delivers the new order (it looks every 5 seconds), and then takes a few seconds to reserve the
    // stock, take the payment and commit, so the moments are spread over about the first nine seconds:
    // before the saga was picked up, and while it is reserving, paying and committing. Which step a
    // given moment falls on varies from run to run; the result must not: the order ends Cancelled, the
    // stock is back, and a payment that went through was refunded.
    [Theory]
    [InlineData(0)]
    [InlineData(2500)]
    [InlineData(4500)]
    [InlineData(5500)]
    [InlineData(6500)]
    [InlineData(8000)]
    public async Task ACancelWhileTheSagaRuns_EndsCancelled_WithTheStockBackAndAnyPaymentRefunded(int millisecondsAfterPlacing)
    {
        var before = await Catalog.StockAsync(Customer, Catalog.Chair);
        var placed = await Customer.PostAsync("/api/orders",
            new { items = new[] { new { productId = Catalog.Chair, quantity = 1 } } }, Guid.NewGuid().ToString());
        placed.Status.Should().Be(HttpStatusCode.Created, "{0}", placed);
        var orderId = placed.Json.Text("id");

        await Task.Delay(millisecondsAfterPlacing);
        await CancelAsync(orderId);

        var cancelled = await Wait.UntilAsync(
            () => Customer.GetAsync($"/api/orders/{orderId}"),
            reply => reply.Status == HttpStatusCode.OK && reply.Json.Text("statusName") is "Cancelled" or "Failed",
            $"order {orderId} to be cancelled", Settle);
        cancelled.Json.Text("statusName").Should().Be("Cancelled", "{0}", cancelled);

        // A message that was still on its way when the order was cancelled (the payment, the stock
        // reservation) arrives about now and has to be undone, so the checks below wait for it
        await Task.Delay(LateMessages);

        var stock = await Wait.UntilAsync(
            () => Catalog.StockAsync(Customer, Catalog.Chair),
            s => s == before, "the stock to be back where it was", TimeSpan.FromSeconds(45));
        stock.Should().Be(before);

        // never charged, or charged and given back: a payment that is still "Succeeded" is money kept
        var payment = await Customer.GetAsync($"/api/payments/order/{orderId}");
        if (payment.Status != HttpStatusCode.NotFound)
        {
            payment.Status.Should().Be(HttpStatusCode.OK, "{0}", payment);
            payment.Json.Prop("status").ToString().Should().BeOneOf(new[] { "Refunded", "3", "Failed", "2" },
                "a cancelled order keeps no money: {0}", payment);
        }
    }

    // Asks for the cancel until the shop takes it. While the payment is being finalized the Order API
    // answers 409 and says to ask again; the order may then be Confirmed, which can be cancelled too.
    private async Task CancelAsync(string orderId)
    {
        var answer = await Wait.UntilAsync(
            () => Customer.DeleteAsync($"/api/orders/{orderId}"),
            reply => reply.Status != HttpStatusCode.Conflict,
            $"the shop to take the cancel of order {orderId}", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(400));
        answer.Status.Should().BeOneOf(new[] { HttpStatusCode.NoContent, HttpStatusCode.Accepted }, "{0}", answer);
    }
}
