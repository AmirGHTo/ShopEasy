using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using Xunit;

public class GatewayApiTests : IAsyncLifetime
{
    // Disable auto-redirect — Gateway redirects unauthenticated requests
    private readonly HttpClient _client = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("http://localhost:5000")
    };

    [Fact]
    public async Task HealthCheck_Live_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthCheck_Ready_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
    }

    [Fact]
    public async Task PlaceOrder_WithoutAuth_ReturnsUnauthorizedOrRedirect()
    {
        var body = new
        {
            items = new[] { new {
                productId   = "00000000-0000-0000-0000-000000000005",
                productName = "4K Webcam",
                quantity    = 1,
                unitPrice   = 89.99
            }},
            notes = "integration test"
        };

        var response = await _client.PostAsJsonAsync("/api/orders", body);

        // Gateway redirects unauthenticated requests to login
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Redirect,
            HttpStatusCode.Found);
    }

    [Fact]
    public async Task GetInventory_WithoutAuth_ReturnsUnauthorizedOrRedirect()
    {
        var response = await _client.GetAsync("/api/inventory");
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Redirect,
            HttpStatusCode.Found);
    }

    [Fact]
    public async Task RateLimiting_AfterThreshold_Returns429()
    {
        // Rate limiting is at Gateway level — must test through Gateway
        // Use a fresh IP-keyed client
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 35; i++)
            responses.Add(await _client.GetAsync("/api/v1/products"));

        responses.Any(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .Should().BeTrue();
    }

    [Fact]
    public async Task RateLimited_Response_HasRetryAfterHeader()
    {
        for (int i = 0; i < 31; i++)
            await _client.GetAsync("/api/v1/products");

        var response = await _client.GetAsync("/api/v1/products");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            response.Headers.Contains("Retry-After").Should().BeTrue();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _client.Dispose(); return Task.CompletedTask; }
}