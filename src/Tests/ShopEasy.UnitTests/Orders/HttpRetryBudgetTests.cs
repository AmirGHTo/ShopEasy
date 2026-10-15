using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The retry budget on an HTTP client built the way Program.cs builds the Product client:
// the budget handler outermost, then the standard Polly resilience handler whose retries ask
// the budget before each retry.
public class HttpRetryBudgetTests
{
    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static HttpClient Client(RetryBudget budget, HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("product")
            .ConfigurePrimaryHttpMessageHandler(() => primary)
            .AddHttpMessageHandler(() => new RetryBudgetHandler(budget))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.ShouldHandle = HttpRetryBudget.ShouldRetry(budget, "test", 2);
            });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("product");
    }

    private static async Task Send(HttpClient client, int requests)
    {
        for (var i = 0; i < requests; i++)
            (await client.GetAsync("http://product/api/products")).Dispose();
    }

    [Fact]
    public async Task WithAGenerousBudget_EveryFailedCallIsRetriedTwice()
    {
        var primary = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var client = Client(new RetryBudget(new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 1000 }), primary);

        await Send(client, 10);

        primary.Calls.Should().Be(30, "10 calls, each tried three times");
    }

    [Fact]
    public async Task WhenEverythingFails_RetriesAreLimitedToTheirShareOfTheTraffic()
    {
        var primary = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var client = Client(new RetryBudget(new RetryBudgetOptions { Ratio = 0.2, MinRetriesPerWindow = 0 }), primary);

        await Send(client, 50);

        // 50 original calls plus the retries the budget allowed, a fifth of 50 at most. Without
        // the budget it would have been 150 calls: three times the load on a failing service.
        primary.Calls.Should().BeInRange(50, 60);
    }

    [Fact]
    public async Task ASuccessfulCall_IsNeverRetried()
    {
        var primary = new CountingHandler(HttpStatusCode.OK);
        var client = Client(new RetryBudget(new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 1000 }), primary);

        await Send(client, 5);

        primary.Calls.Should().Be(5);
    }

    [Fact]
    public async Task AClientError_IsNotRetriedEvenWithBudgetToSpare()
    {
        var primary = new CountingHandler(HttpStatusCode.NotFound);
        var client = Client(new RetryBudget(new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 1000 }), primary);

        await Send(client, 5);

        primary.Calls.Should().Be(5, "a 404 will not become a 200 on the second try");
    }
}
