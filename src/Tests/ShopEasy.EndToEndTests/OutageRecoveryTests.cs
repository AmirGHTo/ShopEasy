namespace ShopEasy.EndToEndTests;

// What happens to an order when a part of the system is taken away while it is being handled, and what
// the system looks like after the part is back. The promise being tested is the one a distributed
// system has to keep: whatever fails, an accepted order is neither lost nor done twice. The order is
// finished once it can be (Confirmed), the customer is charged once, and the stock goes down once.
//
// The vocabulary, because these tests lean on it:
//   outbox        the event that starts the saga is saved in the Order database in the same transaction as
//                 the order, and a background service sends it to the broker later. So accepting an order
//                 does not depend on the broker being up.
//   at-least-once the broker may deliver a message more than once (never "at most once": it would rather
//                 repeat than lose). So every receiver must cope with a repeat (idempotency).
//   saga timeout  an order whose steps do not finish within two minutes is cancelled and compensated.
//                 An outage in these tests is therefore kept well below that.
//
// Each test restores what it broke even when it fails (await using), so the next test starts on a
// whole stack. They run as their own customer, because the order budget of the other two is used.
[Collection(ShopCollection.Name)]
public class OutageRecoveryTests : IAsyncLifetime
{
    // Long enough for the Order service to find the broker again (its own check runs every few seconds),
    // for the consumers to reconnect, and for the saga to run to the end
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(150);

    private Shopper _customer = default!;

    public async Task InitializeAsync() => _customer = await Shopper.SignUpAsync("outage");

    public Task DisposeAsync()
    {
        _customer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WhileTheBrokerIsDown_AnOrderIsStillAccepted_AndIsFinishedOnceTheBrokerIsBack()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Keyboard);
        string orderId;
        decimal total;

        await using (await Chaos.TakeDownAsync("rabbitmq"))
        {
            // The order is accepted without the broker: it and the event that starts its saga are saved
            // together in the database
            var placed = await PlaceAsync(Catalog.Keyboard);
            orderId = placed.Json.Text("id");
            total = placed.Json.Number("totalAmount");

            // The outbox service looks for work every 5 seconds, so twenty seconds lets it try several times
            // against a broker that is not there. The order has to simply wait: not move, not fail, not
            // be lost. (Sending to a broker that is down does not fail; the library holds the message
            // until the connection is back. What would break the promise is an order that needed the
            // broker to be accepted, or one that was dropped while it waited.)
            await Task.Delay(TimeSpan.FromSeconds(20));
            var waiting = await _customer.GetAsync($"/api/orders/{orderId}");
            waiting.Json.Text("statusName").Should().Be("Pending", "{0}", waiting);
        }

        await AssertFinishedOnceAsync(orderId, total, Catalog.Keyboard, before);
    }

    [Fact]
    public async Task WhenThePaymentServiceIsKilledWithAnOrderOnItsWay_TheOrderWaitsAndIsPaidOnce()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Webcam);
        string orderId;
        decimal total;

        // Killed, not stopped: a crash gives the service no chance to finish anything
        await using (await Chaos.TakeDownAsync("payment", crash: true))
        {
            var placed = await PlaceAsync(Catalog.Webcam);
            orderId = placed.Json.Text("id");
            total = placed.Json.Number("totalAmount");

            // The saga goes on until it needs the payment: the stock is reserved (fewer units available,
            // none sold yet), and the command to take the payment waits in the broker's queue, which
            // is durable, for the service that is not there
            await Wait.UntilAsync(
                () => Catalog.StockAsync(_customer, Catalog.Webcam),
                stock => stock.Available == before.Available - 1,
                "the stock to be reserved while the payment service is down", TimeSpan.FromSeconds(45));
            var waiting = await _customer.GetAsync($"/api/orders/{orderId}");
            waiting.Json.Text("statusName").Should().Be("Pending", "it cannot be confirmed without a payment: {0}", waiting);
        }

        await AssertFinishedOnceAsync(orderId, total, Catalog.Webcam, before);
    }

    // The broker has to keep what it holds. A command for a service that is down waits in a queue; if the
    // broker is restarted meanwhile and comes back empty, the command is gone and the saga can only time
    // out and cancel the order. The same loss happens in a smaller way when a restarted broker is empty
    // for the moments before each service has declared its queue again: a command sent in those moments
    // finds no queue and is dropped. Here the queue is durable and the broker keeps its data on a volume,
    // so the command is still there when Payment is back.
    [Fact]
    public async Task ACommandWaitingInTheBrokersQueue_SurvivesARestartOfTheBroker()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Webcam);
        string orderId;
        decimal total;

        await using (await Chaos.TakeDownAsync("payment", crash: true))
        {
            var placed = await PlaceAsync(Catalog.Webcam);
            orderId = placed.Json.Text("id");
            total = placed.Json.Number("totalAmount");

            // The stock is reserved and the command to take the payment is in the queue of the service
            // that is not there
            await Wait.UntilAsync(
                () => Catalog.StockAsync(_customer, Catalog.Webcam),
                stock => stock.Available == before.Available - 1,
                "the stock to be reserved while the payment service is down", TimeSpan.FromSeconds(45));

            await using (await Chaos.TakeDownAsync("rabbitmq"))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        await AssertFinishedOnceAsync(orderId, total, Catalog.Webcam, before);
    }

    [Fact]
    public async Task WhenTheDatabaseGoesAwayInTheMiddleOfTheSaga_TheOrderIsFinishedOnceItIsBack()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Chair);
        var placed = await PlaceAsync(Catalog.Chair);
        var orderId = placed.Json.Text("id");
        var total = placed.Json.Number("totalAmount");

        // Wait until the saga is under way (the stock is reserved), then take away the one thing every
        // service writes to. PostgreSQL is stopped, so the services cannot save anything for a while;
        // they have to wait and retry rather than give the order up. Their patience is minutes (about 7),
        // the saga's own limit is two, which is why the outage is short: this tests that a brief outage
        // is survived, not that a long one is.
        await Wait.UntilAsync(
            () => Catalog.StockAsync(_customer, Catalog.Chair),
            stock => stock.Available == before.Available - 1, "the stock to be reserved", TimeSpan.FromSeconds(45));

        await using (await Chaos.TakeDownAsync("postgres"))
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        await AssertConfirmedAndPaidAsync(orderId, total);
        await AssertUnitsLeftTheShelfAsync(Catalog.Chair, before, units: 1);
    }

    // A crash loop: the services that do the work are killed one after the other, twice over, and a new
    // order is placed just before each kill, so there is always work moving through the one that dies.
    // A kill can land anywhere: in the middle of a message, between saving and acknowledging it, with a
    // command half sent. The broker then delivers the message again (at-least-once), and the receiving
    // service must recognise that it has already done the work (an idempotent consumer). If it did not,
    // a customer would be charged twice or the stock would drop twice; if the work were lost instead,
    // an order would never finish.
    [Fact]
    public async Task WhileOrdersAreInFlight_KillingPaymentInventoryAndOrderInTurn_LosesNothingAndRepeatsNothing()
    {
        var keyboards = await Catalog.StockAsync(_customer, Catalog.Keyboard);
        var webcams = await Catalog.StockAsync(_customer, Catalog.Webcam);

        var orders = new List<(string Id, decimal Total)>();
        var keyboardsOrdered = 0;
        var webcamsOrdered = 0;
        async Task PlaceNextAsync()
        {
            var keyboard = orders.Count % 2 == 0;
            var placed = await PlaceAsync(keyboard ? Catalog.Keyboard : Catalog.Webcam);
            orders.Add((placed.Json.Text("id"), placed.Json.Number("totalAmount")));
            if (keyboard) keyboardsOrdered++; else webcamsOrdered++;
        }

        await PlaceNextAsync();
        for (var round = 1; round <= 2; round++)
        {
            foreach (var service in new[] { "payment", "inventory", "order" })
            {
                await PlaceNextAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(400)); // let it get under way
                await using (await Chaos.TakeDownAsync(service, crash: true))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
                // time to come back and take some work up again before the next one is killed
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
        }

        foreach (var (id, total) in orders)
            await AssertConfirmedAndPaidAsync(id, total);
        await AssertUnitsLeftTheShelfAsync(Catalog.Keyboard, keyboards, keyboardsOrdered);
        await AssertUnitsLeftTheShelfAsync(Catalog.Webcam, webcams, webcamsOrdered);
    }

    // Redis is only a cache of products and stock. Taking it away must cost nothing but speed: the
    // same answers come from the databases, and an order goes through.
    [Fact]
    public async Task WhileRedisIsDown_TheShopStillShowsProductsAndStock_AndTakesOrders()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Desk);
        var price = await Catalog.PriceAsync(_customer, Catalog.Desk);
        string orderId;

        await using (await Chaos.TakeDownAsync("redis"))
        {
            (await Catalog.PriceAsync(_customer, Catalog.Desk)).Should().Be(price);
            (await Catalog.StockAsync(_customer, Catalog.Desk)).Should().Be(before);

            var placed = await PlaceAsync(Catalog.Desk);
            orderId = placed.Json.Text("id");

            // finished while Redis is still away: it is not needed for the order to move
            await AssertConfirmedAndPaidAsync(orderId, placed.Json.Number("totalAmount"));
        }

        await AssertUnitsLeftTheShelfAsync(Catalog.Desk, before, units: 1);
    }

    // Identity issues tokens and signs people in. A service that has seen its signing keys keeps
    // checking tokens without it, the gateway holds the customer's session, and the Order service
    // keeps the token it uses towards Inventory for its lifetime. So a customer who is signed in
    // goes on shopping, and what is lost is signing in or up.
    [Fact]
    public async Task WhileIdentityIsDown_ACustomerWhoIsSignedInKeepsShopping_AndSigningUpWorksAgainOnceItIsBack()
    {
        // The Order service fetches its token the first time it needs one: this order does that, as
        // any shop that has been running for a minute has done
        var warmUp = await PlaceAsync(Catalog.Headphones);
        await AssertConfirmedAndPaidAsync(warmUp.Json.Text("id"), warmUp.Json.Number("totalAmount"));
        var before = await Catalog.StockAsync(_customer, Catalog.Headphones);
        string orderId;

        await using (await Chaos.TakeDownAsync("identity"))
        {
            (await _customer.GetAsync("/api/orders")).Status.Should().Be(HttpStatusCode.OK);
            (await Catalog.StockAsync(_customer, Catalog.Headphones)).Should().Be(before);

            var placed = await PlaceAsync(Catalog.Headphones);
            orderId = placed.Json.Text("id");
            await AssertConfirmedAndPaidAsync(orderId, placed.Json.Number("totalAmount"));
        }

        await AssertUnitsLeftTheShelfAsync(Catalog.Headphones, before, units: 1);

        await Wait.UntilAnswersAsync(new Uri(Stack.Identity, "/health/live"), "Identity to answer again");
        using var newcomer = await Shopper.SignUpAsync("after-identity");
        (await newcomer.GetAsync("/api/orders")).Status.Should().Be(HttpStatusCode.OK);
        // and the session that was open all the time is still good
        (await _customer.GetAsync($"/api/orders/{orderId}")).Status.Should().Be(HttpStatusCode.OK);
    }

    // The gateway keeps the customer's session (the encryption keys of its cookie) in its own
    // memory, so a crash ends every session. What is promised is that nothing else is lost: the
    // order finishes behind it, and the customer, whose browser Identity still recognises, is signed
    // in again by one visit to the sign-in address, with no password.
    [Fact]
    public async Task WhenTheGatewayIsKilled_NothingBehindItIsLost_AndTheCustomerIsSignedInAgainWithoutAPassword()
    {
        var before = await Catalog.StockAsync(_customer, Catalog.Chair);
        var placed = await PlaceAsync(Catalog.Chair);
        var orderId = placed.Json.Text("id");

        await using (await Chaos.TakeDownAsync("gateway", crash: true))
        {
            var refused = async () => await _customer.GetAsync("/api/v1/products");
            await refused.Should().ThrowAsync<HttpRequestException>("nothing answers at the front door");
        }

        await Wait.UntilAnswersAsync(new Uri(Stack.Gateway, "/health"), "the gateway to answer again");

        // Where the new gateway cannot read the old cookie (it keeps its keys in memory where it has
        // no writable home, as in the containers) the customer is a visitor again; here the cookie is
        // dropped, so that this is what is tested wherever the stack runs
        _customer.LoseTheShopSession();
        (await _customer.GetAsync("/account/me")).Status.Should().Be(HttpStatusCode.Unauthorized);
        await _customer.SignInToTheShopAsync();

        await AssertConfirmedAndPaidAsync(orderId, placed.Json.Number("totalAmount"));
        await AssertUnitsLeftTheShelfAsync(Catalog.Chair, before, units: 1);
    }

    private async Task<Reply> PlaceAsync(Guid productId)
    {
        var placed = await _customer.PostAsync("/api/orders",
            new { items = new[] { new { productId, quantity = 1 } } }, Guid.NewGuid().ToString());
        placed.Status.Should().Be(HttpStatusCode.Created, "{0}", placed);
        return placed;
    }

    // The order is Confirmed, the customer was charged the total once, and the stock went down by exactly
    // the one unit ordered: not two (a repeated message acted on twice), not none (a lost one)
    private async Task AssertFinishedOnceAsync(string orderId, decimal total, Guid productId, Stock before)
    {
        await AssertConfirmedAndPaidAsync(orderId, total);
        await AssertUnitsLeftTheShelfAsync(productId, before, units: 1);
    }

    private async Task AssertConfirmedAndPaidAsync(string orderId, decimal total)
    {
        var confirmed = await Wait.UntilAsync(
            () => _customer.GetAsync($"/api/orders/{orderId}"),
            reply => reply.Status == HttpStatusCode.OK && reply.Json.Text("statusName") is "Confirmed" or "Cancelled" or "Failed",
            $"order {orderId} to be finished after the outage", Recovery, TimeSpan.FromSeconds(2));
        confirmed.Json.Text("statusName").Should().Be("Confirmed", "{0}\nWhat the tests did to the stack:\n{1}", confirmed, Chaos.Timeline());

        var payment = await _customer.GetAsync($"/api/payments/order/{orderId}");
        payment.Status.Should().Be(HttpStatusCode.OK, "{0}", payment);
        payment.Json.Number("amount").Should().Be(total, "charged what the order costs, once");
    }

    private async Task AssertUnitsLeftTheShelfAsync(Guid productId, Stock before, int units)
    {
        var stock = await Wait.UntilAsync(
            () => Catalog.StockAsync(_customer, productId),
            s => s.Quantity == before.Quantity - units, "the units to leave the shelf", TimeSpan.FromSeconds(60));
        stock.Should().Be(new Stock(before.Quantity - units, before.Available - units), "as many units gone as were ordered, no more, no fewer");
    }
}
