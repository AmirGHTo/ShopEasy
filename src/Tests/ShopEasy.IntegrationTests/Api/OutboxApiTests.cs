using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

public class OutboxApiTests : IAsyncLifetime
{
    // Outbox endpoints are on OrderService
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://localhost:5102")
    };


    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _client.Dispose(); return Task.CompletedTask; }

    // Monitoring endpoints expose internal state, so they require an admin token.
    [Theory]
    [InlineData("/api/outbox/pending")]
    [InlineData("/api/outbox/delivered")]
    [InlineData("/api/outbox/sagas")]
    [InlineData("/api/outbox/deadletters")]
    public async Task OutboxMonitoring_WithoutToken_ReturnsUnauthorized(string url)
    {
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OutboxDebug_Endpoint_NoLongerExists()
    {
        var response = await _client.GetAsync("/api/outbox/debug");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // The tests below need a JWT with the "admin" role, supplied through the
    // TEST_ADMIN_TOKEN environment variable. They are skipped (return early)
    // when it is not set, like the fault-injection test further down.
    private HttpClient? AdminClient()
    {
        var token = Environment.GetEnvironmentVariable("TEST_ADMIN_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return null;

        var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task DeadLetters_AsAdmin_ReturnsOk()
    {
        using var admin = AdminClient();
        if (admin is null) return;

        var response = await admin.GetAsync("/api/outbox/deadletters");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("totalFailedMessages");
        content.Should().Contain("queues");
    }

    [Fact]
    public async Task Sagas_AsAdmin_ReturnsOk()
    {
        using var admin = AdminClient();
        if (admin is null) return;

        var response = await admin.GetAsync("/api/outbox/sagas");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OrderService_HealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
    }

    [Fact]
    public async Task DeadLetters_WhenNoFailures_HasZeroMessages()
    {
        using var admin = AdminClient();
        if (admin is null) return;

        var response = await admin.GetAsync("/api/outbox/deadletters");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(content);

        // When system is healthy all error queues should be empty
        data.GetProperty("totalFailedMessages").GetInt32()
            .Should().Be(0);

        data.GetProperty("hasFailures").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeadLetters_ReturnsValidStructure()
    {
        using var admin = AdminClient();
        if (admin is null) return;

        var response = await admin.GetAsync("/api/outbox/deadletters");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(content);

        // These fields must always exist
        data.TryGetProperty("totalFailedMessages", out _).Should().BeTrue();
        data.TryGetProperty("hasFailures", out _).Should().BeTrue();
        data.TryGetProperty("queues", out _).Should().BeTrue();

        // totalFailedMessages should be a non-negative number
        data.GetProperty("totalFailedMessages").GetInt32()
            .Should().BeGreaterThanOrEqualTo(0);

        // If there are queues, they should have correct structure
        var queues = data.GetProperty("queues").EnumerateArray().ToList();
        foreach (var queue in queues)
        {
            queue.TryGetProperty("queueName", out _).Should().BeTrue();
            queue.TryGetProperty("messageCount", out _).Should().BeTrue();
            queue.TryGetProperty("managementUrl", out _).Should().BeTrue();
        }
    }

    [Fact]
    [Trait("TestCategory", "FaultInjection")]
    public async Task DeadLetters_AfterFaultInjection_ShowsFailedMessages()
    {
        // This test requires FaultInjection__CommitInventory=true
        // in the inventory service environment
        // Skip if fault injection is not enabled
        var faultEnabled = Environment.GetEnvironmentVariable(
            "FAULT_INJECTION_ENABLED") == "true";

        if (!faultEnabled)
        {
            // Skip test — fault injection not enabled
            return;
        }

        using var admin = AdminClient();
        if (admin is null) return;

        // Wait for a message to fail and land in DLQ
        await Task.Delay(TimeSpan.FromSeconds(10));

        var response = await admin.GetAsync("/api/outbox/deadletters");
        var content = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(content);

        data.GetProperty("hasFailures").GetBoolean()
            .Should().BeTrue();
        data.GetProperty("totalFailedMessages").GetInt32()
            .Should().BeGreaterThan(0);
    }
}