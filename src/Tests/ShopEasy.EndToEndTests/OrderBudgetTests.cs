namespace ShopEasy.EndToEndTests;

// A customer may send 10 order requests a minute. The gateway enforces it per user, and so does the
// Order service for the case that the gateway is bypassed: one customer using up their budget must
// leave everybody else's untouched.
[Collection(ShopCollection.Name)]
public class OrderBudgetTests(ShopFixture shop)
{
    // an order that is refused at once (400) and so costs nothing but the request itself
    private static readonly object Refused = new { items = new[] { new { productId = Catalog.Chair, quantity = 0 } } };

    [Fact]
    public async Task ACustomerWhoUsesUpTheirOrderRequests_DoesNotTakeAnyFromAnotherCustomer()
    {
        using var busy = await Shopper.SignUpAsync("busy");

        for (var request = 1; request <= 10; request++)
        {
            var answer = await busy.PostAsync("/api/orders", Refused);
            answer.Status.Should().Be(HttpStatusCode.BadRequest, "request {0} of the 10 allowed a minute: {1}", request, answer);
        }
        var eleventh = await busy.PostAsync("/api/orders", Refused);
        eleventh.Status.Should().Be(HttpStatusCode.TooManyRequests, "the budget of this customer is used up: {0}", eleventh);

        // somebody else still has theirs
        var other = await shop.Neighbour.PostAsync("/api/orders", Refused);
        other.Status.Should().Be(HttpStatusCode.BadRequest, "another customer is not affected: {0}", other);
    }
}
