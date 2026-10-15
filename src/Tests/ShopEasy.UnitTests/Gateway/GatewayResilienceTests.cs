using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using FluentAssertions;
using ShopEasy.Gateway.Resilience;
using ShopEasy.Shared.Resilience;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The gateway's protection of the calls it proxies, on a real YARP in front of fake services that
// fail in the ways real services fail.
public class GatewayResilienceTests
{
    private static Dictionary<string, string?> Settings(int? activityTimeout = null)
    {
        var settings = new Dictionary<string, string?>();
        if (activityTimeout is { } t) settings["Resilience:Gateway:ActivityTimeoutSeconds"] = t.ToString();
        return settings;
    }

    // ── end to end: a real YARP in front of fake services ────────────────────

    [Theory]
    [InlineData(false)] // the service closes the connection without answering
    [InlineData(true)]  // the connection is reset
    public async Task AGetThatFindsTheConnectionBroken_StillSucceeds_WhenTheServiceIsBackOnTheNextTry(bool reset)
    {
        await using var service = new FlakyService(dropFirst: 1, reset);
        await using var proxy = await ProxyHarness.StartAsync(service.Url);

        var response = await proxy.Client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
        service.Accepted.Should().BeGreaterThan(1, "the first connection failed and the request was repeated");
    }

    [Fact]
    public async Task ARequestWithABody_IsNeverRepeated_BecauseTheServiceMayHaveActedOnIt()
    {
        await using var service = new FlakyService(dropFirst: 100);
        await using var proxy = await ProxyHarness.StartAsync(service.Url);

        var response = await proxy.Client.PostAsync("/api/orders", new StringContent("{\"items\":[]}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        service.Accepted.Should().Be(1, "a second POST /api/orders could place a second order");
    }

    [Fact]
    public async Task AnAnswerFromTheService_IsPassedOnAsItIs_NotRepeated()
    {
        await using var service = await ScriptedService.StartAsync(status: 503);
        await using var proxy = await ProxyHarness.StartAsync(service.Url);

        var response = await proxy.Client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("the service says no");
        service.Hits.Should().Be(1, "a struggling service must not be hit twice by every request");
    }

    [Fact]
    public async Task ARefusedConnection_IsRetriedBrieflyAndThenReported_NotHeldOpen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens on this port any more

        await using var proxy = await ProxyHarness.StartAsync($"http://127.0.0.1:{port}");
        var clock = Stopwatch.StartNew();

        var response = await proxy.Client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AGetToAServiceThatIsRestarting_SucceedsOnceItListensAgain()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // the service is down: connections to this port are refused

        await using var proxy = await ProxyHarness.StartAsync($"http://127.0.0.1:{port}");
        var request = proxy.Client.GetAsync("/api/products"); // refused at first
        await Task.Delay(60);
        await using var service = new FlakyService(dropFirst: 0, port: port); // the service comes back

        var response = await request;

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the gateway retried until the service was listening again");
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task AServiceThatNeverAnswers_ResultsInGatewayTimeout_AndTheRequestToItIsCancelled()
    {
        await using var service = await ScriptedService.StartAsync(status: null); // hangs
        await using var proxy = await ProxyHarness.StartAsync(service.Url, Settings(activityTimeout: 1));
        var clock = Stopwatch.StartNew();

        var response = await proxy.Client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6), "not the 30 seconds of the default timeout");
        service.Hits.Should().Be(1, "a timeout is not retried");
        await UntilTrue(() => service.RequestsCancelled == 1); // the service was told to stop
    }

    private static async Task UntilTrue(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        condition().Should().BeTrue();
    }

    // ── the retry handler on its own: attempts are counted, no network involved ─

    // A handler that fails with the given error for the first N requests, then answers 200
    private sealed class FailingHandler(int failures, HttpRequestError error = HttpRequestError.ConnectionError) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Seen) Seen.Add(request);
            if (Interlocked.Increment(ref _calls) <= failures)
                throw new HttpRequestException(error, "failed");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpMessageInvoker Invoker(HttpMessageHandler inner, int retries = 2, RetryBudgetOptions? budget = null) =>
        new(new SafeRetryHandler(
            inner,
            new RetryBudget(budget ?? new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 1000 }),
            retries,
            delay: _ => TimeSpan.Zero));

    private static Task<HttpResponseMessage> Send(HttpMessageInvoker invoker, HttpMethod method, HttpContent? content = null) =>
        invoker.SendAsync(new HttpRequestMessage(method, "http://service/api/products") { Content = content }, CancellationToken.None);

    [Fact]
    public async Task AGet_IsRetriedAfterAConnectionFailure_AndSucceeds()
    {
        var inner = new FailingHandler(failures: 2);

        var response = await Send(Invoker(inner), HttpMethod.Get);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task AGet_IsRetriedOnlyAsOftenAsConfigured_ThenTheFailureIsReported()
    {
        var inner = new FailingHandler(failures: int.MaxValue);

        var act = () => Send(Invoker(inner, retries: 2), HttpMethod.Get);

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(3, "the first attempt and two retries");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ARequestThatChangesSomething_IsNeverRetried(string method)
    {
        var inner = new FailingHandler(failures: int.MaxValue);

        var act = () => Send(Invoker(inner), new HttpMethod(method));

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AGetWithABody_IsNeverRetried()
    {
        var inner = new FailingHandler(failures: int.MaxValue);

        var act = () => Send(Invoker(inner), HttpMethod.Get, new StringContent("x"));

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AFailureThatIsNotAConnectionFailure_IsNotRetried()
    {
        var inner = new FailingHandler(failures: int.MaxValue, HttpRequestError.SecureConnectionError);

        var act = () => Send(Invoker(inner), HttpMethod.Get);

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task WhenTheRetryBudgetIsUsedUp_AGetIsNotRetried()
    {
        var inner = new FailingHandler(failures: int.MaxValue);
        var noBudget = new RetryBudgetOptions { Ratio = 0, MinRetriesPerWindow = 0 };

        var act = () => Send(Invoker(inner, budget: noBudget), HttpMethod.Get);

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ALimitedBudget_SpreadsRetriesOverManyRequests_NotMoreThanItsShare()
    {
        var inner = new FailingHandler(failures: int.MaxValue);
        var invoker = Invoker(inner, retries: 2, budget: new RetryBudgetOptions { Ratio = 0.2, MinRetriesPerWindow = 0 });

        for (var i = 0; i < 50; i++) await Record.ExceptionAsync(() => Send(invoker, HttpMethod.Get));

        inner.Calls.Should().BeInRange(50, 60, "50 requests plus at most a fifth of that in retries, not 150 calls");
    }

    [Fact]
    public async Task WithRetriesSwitchedOff_AGetIsTriedOnce()
    {
        var inner = new FailingHandler(failures: int.MaxValue);

        var act = () => Send(Invoker(inner, retries: 0), HttpMethod.Get);

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ACaller_ThatGivesUpWhileTheRetryWaits_StopsRetrying()
    {
        var inner = new FailingHandler(failures: int.MaxValue);
        var invoker = new HttpMessageInvoker(new SafeRetryHandler(
            inner, new RetryBudget(new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 1000 }), 5,
            delay: _ => TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://service/"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TheRetry_SendsACopyOfTheRequest_WithTheSameHeadersAndOptions()
    {
        var inner = new FailingHandler(failures: 1);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://service/api/products?x=1")
        {
            Version = HttpVersion.Version11
        };
        request.Headers.Host = "shop.example.com";
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc");
        request.Options.Set(new HttpRequestOptionsKey<string>("route"), "products");

        await Invoker(inner).SendAsync(request, CancellationToken.None);

        inner.Seen.Should().HaveCount(2);
        var retry = inner.Seen[1];
        retry.Should().NotBeSameAs(request, "a request message cannot be sent twice");
        retry.RequestUri.Should().Be(request.RequestUri);
        retry.Method.Should().Be(HttpMethod.Get);
        retry.Version.Should().Be(HttpVersion.Version11);
        retry.Headers.Host.Should().Be("shop.example.com");
        retry.Headers.GetValues("X-Correlation-Id").Should().Equal("abc");
        retry.Options.TryGetValue(new HttpRequestOptionsKey<string>("route"), out var route).Should().BeTrue();
        route.Should().Be("products");
    }

    // ── what counts as a connection failure ──────────────────────────────────

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.NameResolutionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.InvalidResponse, false)]
    [InlineData(HttpRequestError.HttpProtocolError, false)]
    public void OnlyFailuresThatLeaveTheRequestUnansweredAreRetried(HttpRequestError error, bool retried) =>
        SafeRetryHandler.IsConnectionFailure(new HttpRequestException(error, "x")).Should().Be(retried);

    [Fact]
    public void OnlyBodylessGetsAndHeadsCanBeRepeated()
    {
        SafeRetryHandler.CanBeRepeated(new HttpRequestMessage(HttpMethod.Get, "http://x/")).Should().BeTrue();
        SafeRetryHandler.CanBeRepeated(new HttpRequestMessage(HttpMethod.Head, "http://x/")).Should().BeTrue();
        SafeRetryHandler.CanBeRepeated(new HttpRequestMessage(HttpMethod.Post, "http://x/")).Should().BeFalse();
        SafeRetryHandler.CanBeRepeated(new HttpRequestMessage(HttpMethod.Delete, "http://x/")).Should().BeFalse();
        SafeRetryHandler.CanBeRepeated(new HttpRequestMessage(HttpMethod.Get, "http://x/") { Content = new StringContent("x") })
            .Should().BeFalse();
    }

    // ── the timeout for a silent service ─────────────────────────────────────

    [Fact]
    public async Task EveryClusterGetsTheTimeout_UnlessItConfiguresItsOwn()
    {
        var filter = new ServiceTimeoutFilter(Options.Create(new GatewayResilienceOptions { ActivityTimeoutSeconds = 12 }));
        var plain = new ClusterConfig { ClusterId = "a" };
        var own = new ClusterConfig
        {
            ClusterId = "b",
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(5) }
        };
        var other = new ClusterConfig
        {
            ClusterId = "c",
            HttpRequest = new ForwarderRequestConfig { AllowResponseBuffering = true }
        };

        (await filter.ConfigureClusterAsync(plain, default)).HttpRequest!.ActivityTimeout
            .Should().Be(TimeSpan.FromSeconds(12));
        (await filter.ConfigureClusterAsync(own, default)).HttpRequest!.ActivityTimeout
            .Should().Be(TimeSpan.FromMinutes(5), "an explicit setting wins");
        var withOther = (await filter.ConfigureClusterAsync(other, default)).HttpRequest!;
        withOther.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(12));
        withOther.AllowResponseBuffering.Should().BeTrue("the cluster's other settings are kept");
    }

    // ── the configuration ────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(GatewayResilienceOptions.ActivityTimeoutSeconds), 0)]
    [InlineData(nameof(GatewayResilienceOptions.ActivityTimeoutSeconds), 601)]
    [InlineData(nameof(GatewayResilienceOptions.ConnectTimeoutSeconds), 0)]
    [InlineData(nameof(GatewayResilienceOptions.PooledConnectionLifetimeSeconds), 5)]
    [InlineData(nameof(GatewayResilienceOptions.GetRetries), 6)]
    [InlineData(nameof(GatewayResilienceOptions.GetRetries), -1)]
    public void NonsensicalSettings_AreRejected(string property, int value)
    {
        var options = new GatewayResilienceOptions();
        typeof(GatewayResilienceOptions).GetProperty(property)!.SetValue(options, value);

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{property}*");
    }
}
