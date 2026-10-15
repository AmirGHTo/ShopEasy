using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// Settings for the services a browser talks to: believing the headers of the reverse proxy in front
// of them, and the Secure flag of their cookies.
public class WebSecurityTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    // ---- the settings

    [Fact]
    public void TheDefaults_BelieveNoProxy_AndSetTheSecureFlagWhenTheRequestIsSecure()
    {
        var forwarded = ForwardedHeadersSettings.From(Config());

        forwarded.Enabled.Should().BeFalse();
        forwarded.KnownNetworks.Should().BeEmpty();
        forwarded.KnownProxies.Should().BeEmpty();
        WebSecurityOptions.From(Config()).CookieSecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
    }

    [Theory]
    [InlineData("Always", CookieSecurePolicy.Always)]
    [InlineData("SameAsRequest", CookieSecurePolicy.SameAsRequest)]
    [InlineData("None", CookieSecurePolicy.None)]
    [InlineData("always", CookieSecurePolicy.Always)]
    public void TheCookiePolicy_IsReadFromTheConfiguration(string value, CookieSecurePolicy expected) =>
        WebSecurityOptions.From(Config(("Security:CookieSecurePolicy", value))).CookieSecurePolicy.Should().Be(expected);

    [Fact]
    public void ATypoInTheCookiePolicy_StopsTheServiceInsteadOfSilentlyWeakeningIt() =>
        FluentActions.Invoking(() => WebSecurityOptions.From(Config(("Security:CookieSecurePolicy", "Alwayz"))))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void ForwardedHeadersThatAreSwitchedOn_MustSayWhomToBelieve()
    {
        new ForwardedHeadersSettings { Enabled = true }.Invoking(s => s.Validate())
            .Should().Throw<InvalidOperationException>().WithMessage("*KnownNetworks*KnownProxies*");
        new ForwardedHeadersSettings { Enabled = false }.Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("10.0.0.0/8", "")]
    [InlineData("10.0.0.0/8, 172.16.0.0/12", "")]
    [InlineData("10.0.0.0/8;192.168.0.0/16", "")]
    [InlineData("fd00::/8", "")]
    [InlineData("", "10.1.2.3")]
    [InlineData("", "10.1.2.3, ::1")]
    [InlineData("10.0.0.0/8", "10.1.2.3")]
    public void ValidNetworksAndProxies_AreAccepted(string networks, string proxies) =>
        new ForwardedHeadersSettings { Enabled = true, KnownNetworks = networks, KnownProxies = proxies }
            .Invoking(s => s.Validate()).Should().NotThrow();

    [Theory]
    [InlineData("10.0.0.0", "")]          // no prefix length
    [InlineData("10.0.0.0/33", "")]       // too long for IPv4
    [InlineData("10.0.0.0/-1", "")]
    [InlineData("not-an-address/8", "")]
    [InlineData("", "10.0.0.0/8")]        // a network where a single address belongs
    [InlineData("", "not-an-address")]
    public void ANetworkOrProxyThatDoesNotParse_StopsTheServiceAtStartUp(string networks, string proxies) =>
        new ForwardedHeadersSettings { Enabled = true, KnownNetworks = networks, KnownProxies = proxies }
            .Invoking(s => s.Validate()).Should().Throw<InvalidOperationException>();

    // ---- what happens to a request

    private static async Task<(string Scheme, string? RemoteIp, string Host)> Request(
        IConfiguration config, string forwardedProto = "https", string forwardedFor = "203.0.113.5", string forwardedHost = "shop.example.com")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddConfiguredForwardedHeaders(config);
        var app = builder.Build();
        app.UseConfiguredForwardedHeaders(config);
        app.MapGet("/echo", (HttpContext c) => $"{c.Request.Scheme}|{c.Connection.RemoteIpAddress}|{c.Request.Host}");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/echo");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
            using var response = await client.SendAsync(request);
            var parts = (await response.Content.ReadAsStringAsync()).Split('|');
            return (parts[0], parts[1], parts[2]);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AProxyThatIsListed_IsBelieved()
    {
        var (scheme, ip, host) = await Request(Config(
            ("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "127.0.0.1")));

        scheme.Should().Be("https");
        ip.Should().Be("203.0.113.5");
        host.Should().Be("shop.example.com");
    }

    [Fact]
    public async Task ANetworkThatIsListed_IsBelieved()
    {
        var (scheme, ip, _) = await Request(Config(
            ("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownNetworks", "127.0.0.0/8")));

        scheme.Should().Be("https");
        ip.Should().Be("203.0.113.5");
    }

    [Fact]
    public async Task ASenderThatIsNotListed_CannotClaimAnAddressOrScheme()
    {
        var (scheme, ip, host) = await Request(Config(
            ("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies", "10.1.2.3")));

        scheme.Should().Be("http");
        ip.Should().Be("127.0.0.1");
        host.Should().NotBe("shop.example.com");
    }

    [Fact]
    public async Task WhenSwitchedOff_TheHeadersAreIgnored()
    {
        var (scheme, ip, _) = await Request(Config());

        scheme.Should().Be("http");
        ip.Should().Be("127.0.0.1");
    }
}
