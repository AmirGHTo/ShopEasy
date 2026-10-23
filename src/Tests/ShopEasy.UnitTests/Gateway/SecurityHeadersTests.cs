using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The security headers of the gateway, as Program.cs adds them (UseSecurityHeaders), on a free local
// port. The policies are written out in full on purpose: a change to one is a deliberate change here.
public sealed class SecurityHeadersTests : IAsyncLifetime
{
    private const string Storefront =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https:; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private const string Api = "default-src 'none'; frame-ancestors 'none'";

    private const string Operator =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public Task InitializeAsync() => StartAsync(new SecurityHeadersSettings());

    private async Task StartAsync(SecurityHeadersSettings settings)
    {
        if (_app is not null)
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseSecurityHeaders(settings);

        // a service behind the gateway that sets headers of its own: the reverse proxy copies them
        _app.MapGet("/upstream-sets-headers", (HttpContext context) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src *";
            context.Response.Headers.XFrameOptions = "SAMEORIGIN";
            return Results.Text("from a service");
        });
        _app.MapGet("/refused", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        _app.MapGet("/moved", () => Results.Redirect("/elsewhere"));
        _app.MapFallback(() => Results.Text("ok"));
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        // no redirect is followed, so that the answer to the path itself is looked at
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = _client.BaseAddress };
        return await client.GetAsync(path);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "(none)";

    [Theory]
    [InlineData("/")]
    [InlineData("/products")]
    [InlineData("/products?q=desk&category=Furniture")]
    [InlineData("/products/00000000-0000-0000-0000-000000000001")]
    [InlineData("/cart")]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/apiary")]       // starts with "api" but is not under /api
    [InlineData("/portals")]
    [InlineData("/accounting")]
    [InlineData("/healthy")]
    public async Task APageOrFileOfTheStorefront_GetsTheStorefrontPolicy(string path)
    {
        using var response = await GetAsync(path);

        Header(response, "Content-Security-Policy").Should().Be(Storefront);
    }

    [Theory]
    [InlineData("/api/orders")]
    [InlineData("/API/orders")]
    [InlineData("/api/v1/products")]
    [InlineData("/api/outbox/deadletters")] // the Order API's route, not the /outbox page
    [InlineData("/account/me")]
    [InlineData("/account/login")]
    [InlineData("/health")]
    [InlineData("/signin-oidc")]
    [InlineData("/signout-callback-oidc")]
    public async Task AnApiAnswer_AllowsNothing(string path)
    {
        using var response = await GetAsync(path);

        Header(response, "Content-Security-Policy").Should().Be(Api);
    }

    [Theory]
    [InlineData("/portal")]
    [InlineData("/outbox")]
    [InlineData("/sagas")]
    [InlineData("/deadletters")]
    [InlineData("/Sagas")]
    public async Task AnOperatorPage_MayUseItsInlineCode_AndOnlyThose(string path)
    {
        using var response = await GetAsync(path);

        Header(response, "Content-Security-Policy").Should().Be(Operator);
    }

    [Fact]
    public void TheStorefrontPolicy_AllowsNoInlineCodeNoEvalAndNoWildcard()
    {
        Storefront.Should().NotContain("unsafe-inline").And.NotContain("unsafe-eval").And.NotContain(" *").And.NotContain("data:");
        Storefront.Should().Contain("script-src 'self';").And.Contain("frame-ancestors 'none'").And.Contain("object-src 'none'");
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/api/orders")]
    [InlineData("/sagas")]
    [InlineData("/refused")]
    [InlineData("/moved")]
    public async Task EveryAnswer_SaysNoSniffing_NoFraming_LittleReferrer_NoUnusedFeatures(string path)
    {
        using var response = await GetAsync(path);

        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Header(response, "Cross-Origin-Opener-Policy").Should().Be("same-origin");
        Header(response, "Permissions-Policy").Should()
            .Contain("camera=()").And.Contain("microphone=()").And.Contain("geolocation=()").And.Contain("payment=()");
        Header(response, "Content-Security-Policy").Should().NotBe("(none)");
    }

    [Fact]
    public async Task AnAnswerThatIsRefusedOrRedirected_HasTheHeadersToo()
    {
        using var refused = await GetAsync("/refused");
        using var moved = await GetAsync("/moved");

        refused.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        moved.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        Header(refused, "X-Content-Type-Options").Should().Be("nosniff");
        Header(moved, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [Fact]
    public async Task AServiceBehindTheGateway_CannotWeakenThem()
    {
        using var response = await GetAsync("/upstream-sets-headers");

        response.Headers.GetValues("Content-Security-Policy").Should().Equal(Storefront);
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
    }

    [Fact]
    public async Task NamedImageHosts_ReplaceHttps_InTheStorefrontPolicyOnly()
    {
        await StartAsync(new SecurityHeadersSettings { ImageSources = "https://images.example.com https://*.cdn.example.com:8443" });

        using var page = await GetAsync("/products");
        using var api = await GetAsync("/api/orders");
        using var operatorPage = await GetAsync("/sagas");

        Header(page, "Content-Security-Policy").Should()
            .Contain("img-src 'self' https://images.example.com https://*.cdn.example.com:8443;")
            .And.NotContain("img-src 'self' https:;");
        Header(api, "Content-Security-Policy").Should().Be(Api);
        Header(operatorPage, "Content-Security-Policy").Should().Be(Operator);
    }

    [Fact]
    public async Task NoImageSources_MeansTheGatewaysOwnImagesOnly()
    {
        await StartAsync(new SecurityHeadersSettings { ImageSources = "" });

        using var page = await GetAsync("/products");

        Header(page, "Content-Security-Policy").Should().Contain("img-src 'self';");
    }
}

public class SecurityHeadersSettingsTests
{
    [Fact]
    public void TheDefaults_AreOn_AndAnyHttpsImage()
    {
        var settings = new SecurityHeadersSettings();

        settings.Enabled.Should().BeTrue();
        settings.ImageSources.Should().Be("https:");
        settings.Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Fact]
    public void TheSettings_AreReadFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SecurityHeaders:Enabled"] = "false",
            ["SecurityHeaders:ImageSources"] = "https://images.example.com",
        }).Build();

        var settings = SecurityHeadersSettings.From(configuration);

        settings.Enabled.Should().BeFalse();
        settings.ImageSourceList().Should().Equal("https://images.example.com");
    }

    [Fact]
    public void WithNothingConfigured_TheDefaultsApply()
    {
        SecurityHeadersSettings.From(new ConfigurationBuilder().Build()).Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("https:")]
    [InlineData("https://images.example.com")]
    [InlineData("https://images.example.com:8443")]
    [InlineData("https://*.example.com")]
    [InlineData("https://cdn1.example.com, https://cdn2.example.com")]
    [InlineData("HTTPS://IMAGES.EXAMPLE.COM")]
    [InlineData("")]
    public void AnImageSourceThatIsHttps_IsAccepted(string sources)
    {
        new SecurityHeadersSettings { ImageSources = sources }.Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("'unsafe-inline'")]
    [InlineData("'self'")]
    [InlineData("data:")]
    [InlineData("http://images.example.com")]
    [InlineData("http:")]
    [InlineData("https://images.example.com/path")]
    [InlineData("https://")]
    [InlineData("https://a.com;script-src *")]
    [InlineData("https://a.com script-src")]
    [InlineData("https://*")]
    public void AnythingElse_IsRefused_BecauseItWouldEndUpInTheHeader(string sources)
    {
        new SecurityHeadersSettings { ImageSources = sources }.Invoking(s => s.Validate())
            .Should().Throw<InvalidOperationException>().WithMessage("*SecurityHeaders:ImageSources*");
    }
}
