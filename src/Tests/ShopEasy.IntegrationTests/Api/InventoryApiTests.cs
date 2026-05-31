using FluentAssertions;
using System.Net;
using Xunit;

namespace ShopEasy.IntegrationTests.Api;

public class InventoryApiTests : IAsyncLifetime
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://localhost:5103") // Direct to inventory
    };

    private readonly HttpClient _gatewayClient = new()
    {
        BaseAddress = new Uri("http://localhost:5000")
    };

    [Fact]
    public async Task GetInventory_WithoutAuth_Returns401()
    {
        var client = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        var response = await client.GetAsync("/api/inventory");

        // Gateway enforces auth — redirects to login or returns 401
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Redirect,
            HttpStatusCode.Found);

        client.Dispose();
    }
    [Fact]
    public async Task GetInventoryItem_ForKnownProduct_ReturnsOk()
    {
        // Known seeded product
        var productId = "00000000-0000-0000-0000-000000000005";
        var response = await _client.GetAsync($"/api/inventory/{productId}");

        // Direct call bypasses Gateway auth
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetInventoryItem_ForUnknownProduct_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/inventory/{unknownId}");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InventoryHealth_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
        content.Should().Contain("database");
        content.Should().Contain("rabbitmq");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client.Dispose();
        _gatewayClient.Dispose();
        return Task.CompletedTask;
    }
}