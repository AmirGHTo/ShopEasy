namespace ShopEasy.EndToEndTests.Support;

// The customers of one test run. Creating an account is rate limited per client address, so every
// test shares these two instead of signing up its own; the tests of the collection run one after
// the other, which also keeps their readings of the stock free of each other's orders.
// One customer may send 10 order requests a minute (gateway and Order service both limit it) and the
// scenarios of a run use 7 of them for the first customer: one that places more orders has to be
// counted against that, or sign up its own customer (OrderBudgetTests does).
// The second customer places the orders of OrderFailureTests (7 in the same minute at most) and is the
// "another customer" of OrderBudgetTests.
public sealed class ShopFixture : IAsyncLifetime
{
    public Shopper Customer { get; private set; } = default!;
    public Shopper Neighbour { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Customer = await Shopper.SignUpAsync("customer");
        Neighbour = await Shopper.SignUpAsync("neighbour");
    }

    // The gateway keeps its sessions in memory, so when it is restarted every customer is signed out,
    // these two as well. They sign in again the way a browser does (Identity still knows them), so that
    // a test that restarts the gateway does not leave the tests after it without a session. Best effort:
    // the test that did it reports its own failure.
    public async Task SignBackInAsync()
    {
        foreach (var shopper in new[] { Customer, Neighbour })
        {
            try
            {
                await shopper.SignInToTheShopAsync();
            }
            catch (Exception)
            {
                // reported by the tests that find themselves without a session
            }
        }
    }

    public Task DisposeAsync()
    {
        Customer?.Dispose();
        Neighbour?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class ShopCollection : ICollectionFixture<ShopFixture>
{
    public const string Name = "Shop";
}
