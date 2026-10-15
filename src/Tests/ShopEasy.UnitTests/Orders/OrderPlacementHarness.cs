using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Idempotency;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Services;

namespace ShopEasy.UnitTests.Orders;

// The real POST /api/orders action wired to a real DbContext, with the two outside services it
// calls (the product catalog and the stock cache) replaced by fakes whose answers a test controls.
internal sealed class OrderPlacementHarness
{
    public static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");

    public IProductCatalogClient Catalog { get; } = Substitute.For<IProductCatalogClient>();
    public IInventoryStock Stock { get; } = Substitute.For<IInventoryStock>();
    public IdempotencyOptions Settings { get; } = new();

    // What the fakes currently answer; change them between two requests
    public decimal Price { get; set; } = 25m;
    public int Available { get; set; } = 100;

    public readonly JsonSerializerOptions Json = new Microsoft.AspNetCore.Mvc.JsonOptions().JsonSerializerOptions;

    public OrderPlacementHarness()
    {
        Catalog.GetProductsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<Guid, CatalogProduct?>>(
                call.Arg<IEnumerable<Guid>>().Distinct().ToDictionary(
                    id => id, id => (CatalogProduct?)new CatalogProduct(id, "Item " + id.ToString()[^2..], Price, true))));

        Stock.GetStockBatchAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                call.Arg<IEnumerable<Guid>>().Select(id =>
                    new StockInfo(id, "Item " + id.ToString()[^2..], Available, false)).ToList()));
    }

    public sealed record Outcome(IActionResult Result, HttpResponse Response)
    {
        public int StatusCode => Result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            ContentResult c => c.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => throw new InvalidOperationException($"Unexpected result {Result.GetType().Name}")
        };

        public bool Replayed => Response.Headers["Idempotency-Replayed"] == "true";
    }

    // One request, handled by a controller instance with its own scope (DbContext) as in production
    public async Task<Outcome> PlaceAsync(
        OrderDbContext db, PlaceOrderRequest request, string? key = null, string userId = "user-1")
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", userId), new Claim("email", userId + "@example.com")], "test"))
        };

        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns(call =>
            "/api/orders/" + new RouteValueDictionary(call.Arg<UrlActionContext>().Values)["orderId"]);

        var controller = new OrderCommandsController(
            db, NullLogger<OrderCommandsController>.Instance, Stock,
            new OrderReadModelUpdater(db, NullLogger<OrderReadModelUpdater>.Instance),
            Catalog, cancellation: null!, new IdempotencyStore(db),
            Microsoft.Extensions.Options.Options.Create(Settings),
            Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            Url = url
        };

        var result = await controller.PlaceOrder(request, key);
        return new Outcome(result, http.Response);
    }

    // The id of the order in a 201 response, whether it was just created or replayed
    public Guid OrderIdOf(Outcome outcome)
    {
        var json = outcome.Result switch
        {
            CreatedAtActionResult created => JsonSerializer.Serialize(created.Value, Json),
            ContentResult content => content.Content!,
            _ => throw new InvalidOperationException($"No order in {outcome.Result.GetType().Name}")
        };
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    // The JSON of an error response (message and errors), to assert on what the caller is told
    public string ErrorBodyOf(Outcome outcome) =>
        JsonSerializer.Serialize(((ObjectResult)outcome.Result).Value, Json);

    public string BodyOf(Outcome outcome) => outcome.Result switch
    {
        CreatedAtActionResult created => JsonSerializer.Serialize(created.Value, Json),
        ContentResult content => content.Content!,
        _ => throw new InvalidOperationException($"No body in {outcome.Result.GetType().Name}")
    };

    public static PlaceOrderRequest Order(Guid product, int quantity, string? notes = null) =>
        new PlaceOrderRequestBuilder().Item(product, quantity).Notes(notes).Build();
}
