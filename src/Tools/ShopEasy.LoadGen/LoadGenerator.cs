using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ShopEasy.EndToEndTests.Support;

namespace ShopEasy.LoadGen;

// A signed-in customer, as far as the generator needs one (Shopper in the real run, a script in the tests)
public interface IShop : IDisposable
{
    Task<Reply> GetAsync(string path);
    Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null);
    Task<Reply> DeleteAsync(string path);
}

public sealed class ShopperShop(Shopper shopper) : IShop
{
    public Task<Reply> GetAsync(string path) => shopper.GetAsync(path);
    public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null) => shopper.PostAsync(path, body, idempotencyKey);
    public Task<Reply> DeleteAsync(string path) => shopper.DeleteAsync(path);
    public void Dispose() => shopper.Dispose();
}

// Available: units on the shelf that no order in flight has claimed, when the run started
public sealed record Product(Guid Id, decimal Price, int Available);

// Places an order every 60/rate seconds, whatever became of the earlier ones, and follows each one to its end:
//   confirmed  an ordinary order of one to three products; ends Confirmed
//   declined   an order above the payment provider's limit (of a product that has the stock for it); ends Cancelled, nothing charged
//   cancelled  an ordinary order the customer cancels, half of them at once (the saga is still starting) and
//              half once it is confirmed (the refund and the restock run)
// The orders are real: they are in the sales and payment numbers, which is what the dashboards are for.
public sealed class LoadGenerator(
    LoadSettings settings, IReadOnlyList<IShop> customers, TextWriter log, Random random, TimeSpan poll, TimeSpan settle)
{
    private readonly ConcurrentDictionary<string, int> _outcomes = new();
    private (Guid Id, int Quantity)? _decline;

    public IReadOnlyDictionary<string, int> Outcomes => _outcomes;

    public async Task RunAsync(CancellationToken ct)
    {
        var products = await ReadCatalogAsync(customers[0]);
        _decline = LoadPlan.ChooseDecline(products);
        if (_decline is null && settings.Declined > 0)
            log.WriteLine("No product has the stock to go over the payment provider's limit: the declined share is placed as ordinary orders");
        log.WriteLine($"Placing {settings.RatePerMinute} orders a minute for " +
                      (settings.DurationSeconds == 0 ? "as long as it runs" : $"{settings.DurationSeconds} s") +
                      $" with {customers.Count} customer(s); mix confirmed/declined/cancelled = {settings.Confirmed}/{settings.Declined}/{settings.Cancelled}");

        var clock = Stopwatch.StartNew();
        var inFlight = new List<Task>();
        var nextSummary = TimeSpan.FromSeconds(30);
        using var timer = new PeriodicTimer(settings.Interval);
        for (var number = 1; !ct.IsCancellationRequested; number++)
        {
            var intent = LoadPlan.Choose(settings, random.Next(settings.Total));
            var customer = customers[(number - 1) % customers.Count];
            inFlight.Add(OrderAsync(number, customer, intent, products, ct));
            inFlight.RemoveAll(task => task.IsCompleted);

            if (clock.Elapsed >= nextSummary) { Summary(); nextSummary += TimeSpan.FromSeconds(30); }
            if (settings.DurationSeconds > 0 && clock.Elapsed >= TimeSpan.FromSeconds(settings.DurationSeconds)) break;
            try { await timer.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { break; }
        }

        log.WriteLine("Stopped placing orders; waiting for the ones in flight");
        await Task.WhenAll(inFlight).WaitAsync(settle + TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Summary();
    }

    private void Summary() =>
        log.WriteLine("So far: " + (_outcomes.IsEmpty ? "nothing settled yet" : string.Join(", ", _outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"))));

    private async Task<IReadOnlyList<Product>> ReadCatalogAsync(IShop shop)
    {
        var list = await shop.GetAsync("/api/v1/products?pageSize=50");
        if (list.Status != HttpStatusCode.OK)
            throw new InvalidOperationException($"The catalog cannot be read: {list}");
        var products = new List<Product>();
        foreach (var item in list.Json.Prop("items").EnumerateArray())
        {
            var id = Guid.Parse(item.Text("id"));
            var price = item.Number("price");
            if (price <= 0) continue;
            var stock = await shop.GetAsync($"/api/inventory/{id}");
            var available = stock.Status == HttpStatusCode.OK ? (int)stock.Json.Number("availableQuantity") : 0;
            products.Add(new Product(id, price, available));
        }
        return products.Count > 0 ? products : throw new InvalidOperationException("The catalog is empty.");
    }

    private async Task OrderAsync(int number, IShop customer, Intent intent, IReadOnlyList<Product> products, CancellationToken ct)
    {
        string outcome;
        try
        {
            outcome = await PlaceAsync(number, customer, intent, products, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            outcome = "error";
            log.WriteLine($"#{number} {intent}: {error.GetType().Name}: {error.Message}");
        }
        _outcomes.AddOrUpdate(outcome, 1, (_, count) => count + 1);
    }

    private async Task<string> PlaceAsync(int number, IShop customer, Intent intent, IReadOnlyList<Product> products, CancellationToken ct)
    {
        var item = ItemFor(intent, products);
        var placed = await customer.PostAsync("/api/orders",
            new { items = new[] { new { productId = item.Id, quantity = item.Quantity } }, notes = $"demo load #{number}" },
            Guid.NewGuid().ToString());
        if (placed.Status != HttpStatusCode.Created)
        {
            log.WriteLine($"#{number} {intent}: not accepted: {placed}");
            return placed.Status == HttpStatusCode.TooManyRequests ? "throttled" : "refused";
        }
        var orderId = placed.Json.Text("id");

        if (intent == Intent.Cancelled && random.Next(2) == 0)
            return await CancelAsync(customer, orderId);       // at once, while the saga is starting

        var status = await UntilSettledAsync(customer, orderId, ct);
        if (intent == Intent.Cancelled && status == "Confirmed")
            return await CancelAsync(customer, orderId);       // after the confirmation: refund and restock

        return intent == Intent.Declined && status == "Cancelled" ? "declined" : status.ToLowerInvariant();
    }

    private async Task<string> CancelAsync(IShop customer, string orderId)
    {
        var cancel = await customer.DeleteAsync($"/api/orders/{orderId}");
        return cancel.Status is HttpStatusCode.Accepted or HttpStatusCode.NoContent or HttpStatusCode.OK ? "cancelled by customer" : "cancel refused";
    }

    // Pending until the saga has decided: Confirmed, Cancelled or Failed
    private async Task<string> UntilSettledAsync(IShop customer, string orderId, CancellationToken ct)
    {
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < settle)
        {
            var order = await customer.GetAsync($"/api/orders/{orderId}");
            if (order.Status == HttpStatusCode.OK && order.Json.Text("statusName") is var status and not "Pending")
                return status;
            await Task.Delay(poll, ct);
        }
        return "unsettled";
    }

    private (Guid Id, int Quantity) ItemFor(Intent intent, IReadOnlyList<Product> products)
    {
        // a declined order is a product with the stock for it, in the number that goes over the provider's limit;
        // when there is none, an ordinary order
        if (intent == Intent.Declined && _decline is { } decline)
            return decline;
        var inStock = products.Where(p => p.Available >= 3).ToList();
        var pool = inStock.Count > 0 ? inStock : products;
        return (pool[random.Next(pool.Count)].Id, random.Next(1, 4));
    }
}
