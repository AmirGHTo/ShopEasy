using System.Diagnostics;
using System.Net;
using ShopEasy.EndToEndTests.Support;

namespace ShopEasy.Probe;

// What one run of the probe did in one step, and how long it took
public sealed record StepResult(string Step, bool Ok, double Seconds, string? Error = null);

// The customer the probe is, as far as the journey needs: the shop's API, through the gateway
public interface ICustomerSession : IDisposable
{
    Task<Reply> GetAsync(string path);
    Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null);
    Task<Reply> DeleteAsync(string path);
}

// The customer of the end-to-end tests (Shopper), as that interface
public sealed class ShopperSession(Shopper shopper) : ICustomerSession
{
    public Task<Reply> GetAsync(string path) => shopper.GetAsync(path);
    public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null) => shopper.PostAsync(path, body, idempotencyKey);
    public Task<Reply> DeleteAsync(string path) => shopper.DeleteAsync(path);
    public void Dispose() => shopper.Dispose();
}

// The journey of a customer, once: sign in, look at the product, order one, wait for the confirmation,
// cancel (which also sends the refund and puts the stock back). A step that fails ends the run; the steps
// after it are not tried, and are not reported, because nothing is known about them.
public sealed class Journey(ProbeSettings settings, Func<Task<ICustomerSession>> signIn)
{
    public const string SignIn = "sign_in";
    public const string Catalog = "catalog";
    public const string PlaceOrder = "place_order";
    public const string Confirmation = "confirmation";
    public const string Cancel = "cancel";

    // In the order they happen
    public static readonly string[] Steps = [SignIn, Catalog, PlaceOrder, Confirmation, Cancel];

    // The probe's own account: sign in; when Identity does not know the address, create the account once
    // and sign in with it
    public static Func<Task<ICustomerSession>> SigningInAs(ProbeSettings settings) => async () =>
    {
        try
        {
            return new ShopperSession(await Shopper.SignInAsync(settings.Email, settings.Password));
        }
        catch (SignInRefusedException)
        {
            return new ShopperSession(await Shopper.SignUpAsync(settings.Email, settings.Password));
        }
    };

    public async Task<IReadOnlyList<StepResult>> RunAsync(CancellationToken ct)
    {
        var results = new List<StepResult>();
        ICustomerSession? customer = null;
        string? orderId = null;
        try
        {
            if (!await StepAsync(results, SignIn, async () => customer = await signIn(), ct)) return results;

            if (!await StepAsync(results, Catalog, async () =>
                {
                    var product = await customer!.GetAsync($"/api/v1/products/{settings.ProductId}");
                    Expect(product.Status == HttpStatusCode.OK, "the product is not in the catalog", product);
                    Expect(product.Json.Number("price") > 0, "the product has no price", product);
                }, ct)) return results;

            if (!await StepAsync(results, PlaceOrder, async () =>
                {
                    var placed = await customer!.PostAsync("/api/orders",
                        new { items = new[] { new { productId = settings.ProductId, quantity = 1 } }, notes = "synthetic probe" },
                        idempotencyKey: Guid.NewGuid().ToString());
                    Expect(placed.Status == HttpStatusCode.Created, "the order was not accepted", placed);
                    orderId = placed.Json.Text("id");
                }, ct)) return results;

            if (!await StepAsync(results, Confirmation, async () =>
                {
                    var order = await WaitForAsync(customer!, orderId!, "Confirmed", deadEnds: ["Cancelled", "Failed"], ct);
                    Expect(order.Json.Text("paymentStatus") == "Succeeded", "the order is confirmed but not paid", order);
                }, ct)) return results;

            await StepAsync(results, Cancel, async () =>
            {
                var cancel = await customer!.DeleteAsync($"/api/orders/{orderId}");
                Expect(cancel.Status is HttpStatusCode.Accepted or HttpStatusCode.NoContent, "the cancellation was not accepted", cancel);
                var order = await WaitForAsync(customer, orderId!, "Cancelled", deadEnds: ["Failed"], ct);
                Expect(order.Json.Text("paymentStatus") == "Refunded", "the order is cancelled but the money was not given back", order);
                orderId = null; // nothing is left to clean up
            }, ct);
            return results;
        }
        finally
        {
            // A run that failed after the order was placed leaves an order in flight: cancel it now rather
            // than wait for the saga to time out, so a long outage does not leave an order per minute behind
            if (customer is not null && orderId is not null)
                try { await customer.DeleteAsync($"/api/orders/{orderId}"); } catch { /* the saga's timeout settles it */ }
            customer?.Dispose();
        }
    }

    private async Task<bool> StepAsync(List<StepResult> results, string step, Func<Task> body, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            // a step cannot take longer than a few of its own waits, whatever the shop does
            await body().WaitAsync(settings.StepTimeout * 3, ct);
            results.Add(new StepResult(step, true, Stopwatch.GetElapsedTime(started).TotalSeconds));
            return true;
        }
        catch (Exception error)
        {
            // The container is stopping: whatever the step said (it may well be an error, cut short) is not
            // about the shop, and the run is not reported
            ct.ThrowIfCancellationRequested();
            results.Add(new StepResult(step, false, Stopwatch.GetElapsedTime(started).TotalSeconds, Short(error)));
            return false;
        }
    }

    // The order, as soon as its status is the wanted one. A status from which it can never get there
    // (a cancelled order will not be confirmed) is a failure at once; any other is a step on the way
    // (a confirmed order is still confirmed while its refund is on the way).
    private async Task<Reply> WaitForAsync(ICustomerSession customer, string orderId, string wanted, string[] deadEnds, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var limit = started + settings.StepTimeout;
        Reply last;
        while (true)
        {
            last = await customer.GetAsync($"/api/orders/{orderId}");
            if (last.Status == HttpStatusCode.OK)
            {
                var status = last.Json.Text("statusName");
                if (status == wanted) return last;
                if (deadEnds.Contains(status))
                    throw new InvalidOperationException($"the order ended as {status} instead of {wanted}: {last}");
            }
            if (DateTime.UtcNow >= limit)
                throw new TimeoutException($"the order was not {wanted} after {settings.StepTimeout.TotalSeconds:0} s. Last seen: {last}");
            await Task.Delay(settings.PollAfter(DateTime.UtcNow - started), ct);
        }
    }

    private static void Expect(bool condition, string what, Reply answer)
    {
        if (!condition) throw new InvalidOperationException($"{what}: {answer}");
    }

    private static string Short(Exception error)
    {
        var text = error is TimeoutException or InvalidOperationException ? error.Message : $"{error.GetType().Name}: {error.Message}";
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
