extern alias LoadGenAssembly;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LoadGenAssembly::ShopEasy.EndToEndTests.Support;
using LoadGenAssembly::ShopEasy.LoadGen;
using Xunit;

namespace ShopEasy.UnitTests.Demo;

// The demo load generator. (The load generator compiles the same customer sources as the synthetic probe, so
// its assembly is referenced under an alias.)
public class LoadSettingsTests
{
    private static (LoadSettings? Settings, string? Problem) Parse(string[] args, Dictionary<string, string>? environment = null) =>
        LoadSettings.Parse(args, name => environment?.GetValueOrDefault(name));

    [Fact]
    public void WithoutAnything_ItIsTwelveOrdersAMinute_UntilItIsStopped_WithTheUsualMix()
    {
        var (settings, problem) = Parse([]);

        problem.Should().BeNull();
        settings.Should().Be(new LoadSettings(12, 0, 70, 10, 20));
    }

    [Fact]
    public void TheEnvironment_IsWhatComposeHands_AndAnArgumentWinsOverIt()
    {
        var environment = new Dictionary<string, string> { ["LOAD_RATE_PER_MINUTE"] = "30", ["LOAD_DURATION_SECONDS"] = "300", ["LOAD_MIX"] = "50,25,25" };

        Parse([], environment).Settings.Should().Be(new LoadSettings(30, 300, 50, 25, 25));
        Parse(["--rate", "6", "--mix", "100,0,0"], environment).Settings.Should().Be(new LoadSettings(6, 300, 100, 0, 0));
    }

    [Theory]
    [InlineData("--rate", "0")]
    [InlineData("--rate", "81")]
    [InlineData("--rate", "many")]
    [InlineData("--duration", "-1")]
    [InlineData("--mix", "70,30")]
    [InlineData("--mix", "0,0,0")]
    [InlineData("--mix", "70,-10,40")]
    [InlineData("--mix", "a,b,c")]
    [InlineData("--speed", "3")]
    public void ASettingThatMakesNoSense_IsRefused_WithWhatToWrite(string option, string value)
    {
        var (settings, problem) = Parse([option, value]);

        settings.Should().BeNull();
        problem.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(12, 2)]
    [InlineData(80, 10)]
    public void ACustomerIsAskedForAtMostEightOrdersAMinute_BecauseTheLimitIsTen(int rate, int customers)
        => new LoadSettings(rate, 0, 1, 0, 0).Customers.Should().Be(customers);

    [Fact]
    public void TheTopRate_NeedsNoMoreCustomersThanOneClientAddressMaySignUpAnHour()
        => new LoadSettings(LoadSettings.MaxRatePerMinute, 0, 1, 0, 0).Customers.Should().BeLessThanOrEqualTo(10);

    [Fact]
    public void TheInterval_IsASixtiethOfTheRate()
        => new LoadSettings(30, 0, 1, 0, 0).Interval.Should().Be(TimeSpan.FromSeconds(2));
}

public class LoadPlanTests
{
    private static readonly LoadSettings Mix = new(12, 0, 70, 10, 20);

    [Theory]
    [InlineData(0, Intent.Confirmed)]
    [InlineData(69, Intent.Confirmed)]
    [InlineData(70, Intent.Declined)]
    [InlineData(79, Intent.Declined)]
    [InlineData(80, Intent.Cancelled)]
    [InlineData(99, Intent.Cancelled)]
    public void TheTicket_ChoosesByTheShares(int ticket, Intent expected)
        => LoadPlan.Choose(Mix, ticket).Should().Be(expected);

    [Theory]
    [InlineData(299.99, 34)]
    [InlineData(10_000, 2)]
    [InlineData(10_001, 1)]
    [InlineData(150, 67)]
    public void ADeclinedOrder_IsAlwaysAboveTheProvidersLimit_ByTheFewestUnits(double price, int quantity)
    {
        LoadPlan.DeclineQuantity((decimal)price).Should().Be(quantity);
        (quantity * (decimal)price).Should().BeGreaterThan(LoadPlan.ProviderLimit);
        ((quantity - 1) * (decimal)price).Should().BeLessThanOrEqualTo(LoadPlan.ProviderLimit);
    }

    private static Product P(string id, decimal price, int available) => new(Guid.Parse($"00000000-0000-0000-0000-{id.PadLeft(12, '0')}"), price, available);

    [Fact]
    public void TheDeclinedOrder_IsOfTheProductWithTheMostRoomOnTheShelf_NotJustTheDearest()
    {
        // the dearest has only 10 on the shelf (it would be refused for stock and never reach the provider)
        var chosen = LoadPlan.ChooseDecline([P("1", 800, 10), P("2", 299.99m, 40), P("3", 299.99m, 300)]);

        chosen.Should().Be((P("3", 0, 0).Id, 34));
    }

    [Fact]
    public void WhenNoProductHasTheStockToGoOverTheLimit_ThereIsNoDeclinedOrder()
        => LoadPlan.ChooseDecline([P("1", 800, 10), P("2", 299.99m, 33), P("3", 5, 1000)]).Should().BeNull();

    [Theory]
    [InlineData(99)]       // it would take 102 of them, and an order holds 100 of a product
    [InlineData(0)]
    [InlineData(-5)]
    public void AProductThatCannotGoOverTheLimit_HasNoDeclineQuantity(double price)
        => LoadPlan.DeclineQuantity((decimal)price).Should().BeNull();
}

public class LoadGeneratorTests
{
    private static readonly Guid Cheap = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Dear = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static Task<Reply> Answer(HttpStatusCode status, object body) =>
        Reply.ReadAsync(new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });

    // The shop as the generator meets it: a catalog of two products, orders that are Pending for two looks and
    // then decided by the script, and a record of everything that was asked.
    private sealed class FakeShop(
        Func<string, int, string>? decide = null, HttpStatusCode placeStatus = HttpStatusCode.Created, int dearStock = 40) : IShop
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _looks = [];
        private int _orders;

        public List<string> Calls { get; } = [];
        public List<(Guid Product, int Quantity)> Placed { get; } = [];

        // (order id, looks so far) -> status; the default is Confirmed after two looks
        private Func<string, int, string> Decide { get; } = decide ?? ((_, looks) => looks < 2 ? "Pending" : "Confirmed");

        public Task<Reply> GetAsync(string path)
        {
            lock (_gate) Calls.Add("GET " + path);
            if (path.StartsWith("/api/v1/products", StringComparison.Ordinal))
                return Answer(HttpStatusCode.OK, new
                {
                    items = new[] { new { id = Cheap, price = 20m }, new { id = Dear, price = 299.99m } },
                });
            var id = path[(path.LastIndexOf('/') + 1)..];
            if (path.StartsWith("/api/inventory/", StringComparison.Ordinal))
                return Answer(HttpStatusCode.OK, new { quantity = 100, availableQuantity = id == Dear.ToString() ? dearStock : 100 });
            int looks;
            lock (_gate) looks = _looks[id] = _looks.GetValueOrDefault(id) + 1;
            return Answer(HttpStatusCode.OK, new { statusName = Decide(id, looks) });
        }

        public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null)
        {
            var item = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.GetProperty("items")[0];
            lock (_gate)
            {
                Calls.Add("POST " + path);
                if (placeStatus != HttpStatusCode.Created) return Answer(placeStatus, new { error = "no" });
                Placed.Add((item.GetProperty("productId").GetGuid(), item.GetProperty("quantity").GetInt32()));
                return Answer(HttpStatusCode.Created, new { id = $"order-{++_orders}" });
            }
        }

        public Task<Reply> DeleteAsync(string path)
        {
            lock (_gate) Calls.Add("DELETE " + path);
            return Answer(HttpStatusCode.Accepted, new { });
        }

        public void Dispose() { }
    }

    private static async Task<(LoadGenerator Generator, FakeShop Shop)> RunAsync(
        LoadSettings settings, FakeShop? shop = null, int seed = 1)
    {
        shop ??= new FakeShop();
        var generator = new LoadGenerator(settings, [shop], TextWriter.Null, new Random(seed),
            poll: TimeSpan.FromMilliseconds(5), settle: TimeSpan.FromSeconds(5));
        await generator.RunAsync(CancellationToken.None);
        return (generator, shop);
    }

    // 80 orders a minute is one every 0.75 s: a run of one second places two orders
    private static LoadSettings Short(int confirmed, int declined, int cancelled) => new(80, 1, confirmed, declined, cancelled);

    [Fact]
    public async Task ConfirmedOrders_AreOrdinaryOrders_FollowedUntilTheyAreConfirmed()
    {
        var (generator, shop) = await RunAsync(Short(1, 0, 0));

        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("confirmed");
        generator.Outcomes["confirmed"].Should().Be(shop.Placed.Count).And.BeGreaterThanOrEqualTo(2);
        shop.Placed.Should().OnlyContain(p => p.Quantity >= 1 && p.Quantity <= 3);
        shop.Calls.Should().NotContain(c => c.StartsWith("DELETE"), "an order that is confirmed is left alone");
    }

    [Fact]
    public async Task ADeclinedOrder_IsTheDearestProduct_InTheNumberThatGoesOverTheProvidersLimit()
    {
        var shop = new FakeShop(decide: (_, looks) => looks < 2 ? "Pending" : "Cancelled");

        var (generator, _) = await RunAsync(Short(0, 1, 0), shop);

        shop.Placed.Should().OnlyContain(p => p.Product == Dear && p.Quantity == 34);
        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("declined");
    }

    [Fact]
    public async Task WhenTheDearProductIsShortOfStock_TheDeclinedShareIsPlacedAsOrdinaryOrders()
    {
        // 34 would be refused for stock before the provider ever saw them; the cheap one cannot reach 10,000 in 100
        var shop = new FakeShop(dearStock: 10);

        var (generator, _) = await RunAsync(Short(0, 1, 0), shop);

        shop.Placed.Should().OnlyContain(p => p.Quantity <= 3);
        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("confirmed");
    }

    [Fact]
    public async Task ACancelledOrder_IsCancelledByTheCustomer_EitherAtOnceOrOnceItIsConfirmed()
    {
        var (generator, shop) = await RunAsync(Short(0, 0, 1), seed: 7);

        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("cancelled by customer");
        shop.Calls.Count(c => c.StartsWith("DELETE")).Should().Be(shop.Placed.Count, "every one of them is cancelled, once");
    }

    [Fact]
    public async Task AnOrderThatTheShopRefusesBecauseOfALimit_IsCountedAsThrottled_NotAsAnError()
    {
        var (generator, _) = await RunAsync(Short(1, 0, 0), new FakeShop(placeStatus: HttpStatusCode.TooManyRequests));

        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("throttled");
    }

    [Fact]
    public async Task AnOrderThatNeverSettles_IsReported_AsUnsettled_AfterTheLimit()
    {
        var shop = new FakeShop(decide: (_, _) => "Pending");
        var generator = new LoadGenerator(Short(1, 0, 0), [shop], TextWriter.Null, new Random(1),
            poll: TimeSpan.FromMilliseconds(5), settle: TimeSpan.FromMilliseconds(100));

        await generator.RunAsync(CancellationToken.None);

        generator.Outcomes.Should().ContainSingle().Which.Key.Should().Be("unsettled");
    }

    [Fact]
    public async Task TheOrders_AreSharedOutBetweenTheCustomers()
    {
        var first = new FakeShop();
        var second = new FakeShop();
        var generator = new LoadGenerator(Short(1, 0, 0), [first, second], TextWriter.Null, new Random(1),
            poll: TimeSpan.FromMilliseconds(5), settle: TimeSpan.FromSeconds(5));

        await generator.RunAsync(CancellationToken.None);

        first.Placed.Should().NotBeEmpty();
        second.Placed.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnEmptyCatalog_StopsTheRun_WithAMessage()
    {
        var shop = new EmptyCatalog();
        var generator = new LoadGenerator(Short(1, 0, 0), [shop], TextWriter.Null, new Random(1), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(1));

        var run = () => generator.RunAsync(CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*catalog*");
    }

    private sealed class EmptyCatalog : IShop
    {
        public Task<Reply> GetAsync(string path) => Answer(HttpStatusCode.OK, new { items = Array.Empty<object>() });
        public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null) => throw new NotSupportedException();
        public Task<Reply> DeleteAsync(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
