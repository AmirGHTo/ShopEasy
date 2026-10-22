using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// A request that changes something under /api has to carry X-Requested-With, as a page of another
// site cannot add it. The tests start the middleware as Program.cs registers it, on a free local port.
public sealed class RequestedWithHeaderTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseRequestedWithHeaderCheck();
        _app.MapFallback(() => Results.Text("reached"));
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> SendAsync(string method, string path, string? header = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (header is not null)
            request.Headers.TryAddWithoutValidation(RequestedWithHeader.Name, header);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ARequestThatChangesSomething_WithoutTheHeader_IsRefused(string method)
    {
        var response = await SendAsync(method, "/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("request_header_missing");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ARequestThatChangesSomething_WithTheHeader_GoesThrough(string method)
    {
        var response = await SendAsync(method, "/api/orders", header: "fetch");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("reached");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyHeader_IsRefused(string value)
    {
        var response = await SendAsync("POST", "/api/orders", header: value);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ARequestThatOnlyReads_NeedsNoHeader(string method)
    {
        var response = await SendAsync(method, "/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/account/logout")]
    [InlineData("/health/live")]
    [InlineData("/apiary")]
    public async Task APathOutsideApi_IsNotChecked(string path)
    {
        var response = await SendAsync("POST", path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/API/orders")]
    [InlineData("/Api/Orders")]
    public async Task ThePath_IsMatchedWithoutRegardToCase(string path)
    {
        var response = await SendAsync("POST", path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
