using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShopEasy.OrderService.Services;
using System.Net;
using System.Text;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

public class ProductCatalogClientTests
{
    private static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static ProductCatalogClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://product:8080") },
            NullLogger<ProductCatalogClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Shape of ProductResponseV1 returned by GET /api/v1/products/{id}
    private static string ProductJson(Guid id, string name, decimal price, bool isActive) =>
        $$"""
        {"id":"{{id}}","name":"{{name}}","description":"d","price":{{price.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "category":"c","imageUrl":"","isActive":{{isActive.ToString().ToLowerInvariant()}}}
        """;

    [Fact]
    public async Task ReturnsProductFromTheProductService()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.OK, ProductJson(Webcam, "4K Webcam", 89.99m, true)));

        var result = await CreateClient(handler).GetProductsAsync([Webcam]);

        result[Webcam].Should().Be(new CatalogProduct(Webcam, "4K Webcam", 89.99m, true));
        handler.Requests.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be($"/api/v1/products/{Webcam}");
    }

    [Fact]
    public async Task NotFound_IsReportedAsNull_NotAsAnError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await CreateClient(handler).GetProductsAsync([Webcam]);

        result.ContainsKey(Webcam).Should().BeTrue();
        result[Webcam].Should().BeNull();
    }

    [Fact]
    public async Task InactiveProduct_IsReturnedWithIsActiveFalse()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.OK, ProductJson(Webcam, "4K Webcam", 89.99m, false)));

        var result = await CreateClient(handler).GetProductsAsync([Webcam]);

        result[Webcam]!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateIds_AreOnlyLookedUpOnce()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.OK, ProductJson(Webcam, "4K Webcam", 89.99m, true)));

        var result = await CreateClient(handler).GetProductsAsync([Webcam, Webcam, Webcam]);

        result.Count.Should().Be(1);
        handler.Requests.Count.Should().Be(1);
    }

    [Fact]
    public async Task ServerError_ThrowsProductCatalogUnavailable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => CreateClient(handler).GetProductsAsync([Webcam]);

        await act.Should().ThrowAsync<ProductCatalogUnavailableException>();
    }

    [Fact]
    public async Task NetworkFailure_ThrowsProductCatalogUnavailable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var act = () => CreateClient(handler).GetProductsAsync([Webcam]);

        await act.Should().ThrowAsync<ProductCatalogUnavailableException>();
    }

    [Fact]
    public async Task MalformedBody_ThrowsProductCatalogUnavailable()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "this is not json"));

        var act = () => CreateClient(handler).GetProductsAsync([Webcam]);

        await act.Should().ThrowAsync<ProductCatalogUnavailableException>();
    }

    [Fact]
    public async Task OneFailingLookup_FailsTheWholeBatch()
    {
        var other = Guid.NewGuid();
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(other.ToString())
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(HttpStatusCode.OK, ProductJson(Webcam, "4K Webcam", 89.99m, true)));

        var act = () => CreateClient(handler).GetProductsAsync([Webcam, other]);

        await act.Should().ThrowAsync<ProductCatalogUnavailableException>();
    }
}
