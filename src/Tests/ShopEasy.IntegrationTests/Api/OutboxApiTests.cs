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

    [Fact]
    public async Task OutboxPending_Endpoint_ReturnsOkOrUnauthorized()
    {
        var response = await _client.GetAsync("/api/outbox/pending");
        // Returns 200 (AllowAnonymous on the endpoint) or 401 if auth enforced
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OutboxDebug_Endpoint_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/outbox/debug");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("tables");
    }

    [Fact]
    public async Task DeadLetters_Endpoint_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/outbox/deadletters");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("totalFailedMessages");
        content.Should().Contain("queues");
    }

    [Fact]
    public async Task Sagas_Endpoint_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/outbox/sagas");
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
        var response = await _client.GetAsync("/api/outbox/deadletters");
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
        var response = await _client.GetAsync("/api/outbox/deadletters");
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

        // Wait for a message to fail and land in DLQ
        await Task.Delay(TimeSpan.FromSeconds(10));

        var response = await _client.GetAsync("/api/outbox/deadletters");
        var content = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(content);

        data.GetProperty("hasFailures").GetBoolean()
            .Should().BeTrue();
        data.GetProperty("totalFailedMessages").GetInt32()
            .Should().BeGreaterThan(0);
    }
}