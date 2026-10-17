namespace ShopEasy.EndToEndTests.Support;

// The customers of one test run. Creating an account is rate limited per client address, so every
// test shares these two instead of signing up its own; the tests of the collection run one after
// the other, which also keeps their readings of the stock free of each other's orders.
// One customer may send 10 order requests a minute (gateway and Order service both limit it) and the
// scenarios of a run use 7 of them for the first customer: one that places more orders has to be
// counted against that, or sign up its own customer (OrderBudgetTests does).
public sealed class ShopFixture : IAsyncLifetime
{
    public Shopper Customer { get; private set; } = default!;
    public Shopper Neighbour { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Customer = await Shopper.SignUpAsync("customer");
        Neighbour = await Shopper.SignUpAsync("neighbour");
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
