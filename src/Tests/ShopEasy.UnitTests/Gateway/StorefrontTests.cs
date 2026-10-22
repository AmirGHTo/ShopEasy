using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ShopEasy.Gateway.Storefront;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The gateway serves the built storefront. These tests start the same wiring (UseStorefrontFiles and
// MapStorefront, as Program.cs calls them) on a free local port over a throw-away web root.
public sealed class StorefrontTests : IAsyncLifetime
{
    private const string IndexHtml = "<!doctype html><div id=\"root\"></div>";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "storefront-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), IndexHtml);
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "app-3f9c1a.js"), "console.log('shop')");
        await StartAsync();
    }

    private async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _webRoot, ContentRootPath = _webRoot });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.UseStorefrontFiles();
        // an endpoint that exists, as the proxied APIs and /account/login do in the gateway
        _app.MapGet("/api/orders", () => Results.Text("[]", "application/json"));
        _app.MapGet("/account/me", () => Results.Text("me"));
        _app.MapStorefront();
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Fact]
    public async Task TheRoot_IsTheApp_AndIsCheckedOnEveryVisit()
    {
        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(IndexHtml);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/products/00000000-0000-0000-0000-0000000000a1")]
    [InlineData("/cart")]
    public async Task APageAddressOfTheApp_GetsTheApp_SoRefreshingAndBookmarksWork(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(IndexHtml);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task AFileOfTheBuild_IsCachedForGood()
    {
        var response = await _client.GetAsync("/assets/app-3f9c1a.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("console.log");
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromDays(365));
        response.Headers.CacheControl.ToString().Should().Contain("immutable");
    }

    [Theory]
    [InlineData("/api/nothing-here")]
    [InlineData("/api/v1/products/nothing")]
    [InlineData("/account/nothing-here")]
    public async Task APathUnderApiOrAccountThatNothingHandles_IsA404_NeverHtml(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("root");
    }

    [Fact]
    public async Task EndpointsThatExist_AreNotShadowedByTheApp()
    {
        (await _client.GetStringAsync("/api/orders")).Should().Be("[]");
        (await _client.GetStringAsync("/account/me")).Should().Be("me");
    }

    [Fact]
    public async Task OnlyAGetIsAnsweredWithThePage()
    {
        var response = await _client.PostAsync("/cart", new StringContent(""));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("root");
    }

    [Fact]
    public async Task WithoutABuiltApp_ThePageAddressesAreSimply404()
    {
        File.Delete(Path.Combine(_webRoot, "index.html"));

        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
