namespace ShopEasy.EndToEndTests;

// Who may see what, as the gateway and the services enforce it together: a visitor, a customer and
// another customer, each with a real session.
[Collection(ShopCollection.Name)]
public class AccessThroughTheGatewayTests(ShopFixture shop)
{
    [Fact]
    public async Task AVisitor_SeesTheCatalog_ButNotOrdersAndCannotPlaceOne()
    {
        using var visitor = Shopper.Visitor();

        var catalog = await visitor.GetAsync("/api/v1/products");
        catalog.Status.Should().Be(HttpStatusCode.OK, "{0}", catalog);
        catalog.Json.Prop("items").GetArrayLength().Should().BeGreaterThan(0);

        // without a session the gateway sends the visitor to sign in (API clients see 401)
        var orders = await visitor.GetAsync("/api/orders");
        orders.Status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.Found);
        var placing = await visitor.PostAsync("/api/orders",
            new { items = new[] { new { productId = Catalog.Chair, quantity = 1 } } });
        placing.Status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.Found);
    }

    [Fact]
    public async Task ACustomer_IsKnownToTheGateway_ByTheAccountTheyCreated()
    {
        var me = await shop.Customer.GetAsync("/account/me");

        me.Status.Should().Be(HttpStatusCode.OK, "{0}", me);
        me.Json.Text("email").Should().Be(shop.Customer.Email);
        me.Json.Prop("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("customer");
    }

    [Fact]
    public async Task AnotherCustomer_CannotSeeAnOrderOrItsPayment_AndNobodyButAnAdministratorSeesTheMonitoring()
    {
        // an order of the first customer, to look for
        var placed = await shop.Customer.PostAsync("/api/orders",
            new { items = new[] { new { productId = Catalog.Chair, quantity = 1 } } },
            idempotencyKey: Guid.NewGuid().ToString());
        placed.Status.Should().Be(HttpStatusCode.Created, "{0}", placed);
        var orderId = placed.Json.Text("id");

        // another customer is told it does not exist, and may not read its payment
        (await shop.Neighbour.GetAsync($"/api/orders/{orderId}")).Status.Should().Be(HttpStatusCode.NotFound);
        var theirList = await shop.Neighbour.GetAsync("/api/orders?pageSize=50");
        theirList.Json.Prop("items").EnumerateArray().Should().NotContain(o => o.Text("orderId") == orderId);
        await Wait.UntilAsync(
            () => shop.Customer.GetAsync($"/api/payments/order/{orderId}"),
            payment => payment.Status == HttpStatusCode.OK, "the payment to exist", TimeSpan.FromSeconds(60));
        (await shop.Neighbour.GetAsync($"/api/payments/order/{orderId}")).Status.Should().Be(HttpStatusCode.Forbidden);

        // monitoring is for administrators: a customer gets a refusal, not data
        (await shop.Customer.GetAsync("/api/outbox/sagas")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await shop.Customer.GetAsync("/api/payments")).Status.Should().Be(HttpStatusCode.Forbidden);
    }
}
