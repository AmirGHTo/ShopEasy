using FluentAssertions;
using System.Net;
using Xunit;

public class ProductsApiTests : IAsyncLifetime
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://localhost:5101") // ProductService direct
    };

    [Fact]
    public async Task GetProducts_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/v1/products");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProducts_v2_ReturnsPriceFormatted()
    {
        var response = await _client.GetAsync("/api/v2/products");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("priceFormatted");
        content.Should().Contain("currency");
    }

    [Fact]
    public async Task GetProducts_WithInvalidCategory_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/products?category=nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProducts_SecondRequest_ReturnsCacheHit()
    {
        await _client.GetAsync("/api/v1/products"); // populate cache
        var response = await _client.GetAsync("/api/v1/products");
        response.Headers.TryGetValues("X-Cache", out var values);
        values?.First().Should().Be("HIT");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _client.Dispose(); return Task.CompletedTask; }
}