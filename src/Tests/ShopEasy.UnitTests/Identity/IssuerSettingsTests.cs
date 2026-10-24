using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Duende.IdentityServer.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopEasy.Identity.Configuration;
using ShopEasy.UnitTests.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Identity;

// The issuer every token names (IdentityServer:IssuerUri). Identity is reached by an internal name
// (the gateway, the APIs) and a public one (browsers); without a fixed issuer the two see different
// ones, and the ID token the gateway holds is refused when a browser asks Identity to end the session.
public class IssuerSettingsTests
{
    // ---- validation

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5001")]
    [InlineData("https://identity.example.com")]
    [InlineData("https://identity.example.com:8443")]
    public void AnAddressWithoutAPath_IsAccepted(string issuer)
    {
        new IssuerSettings { IssuerUri = issuer }.Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("localhost:5001")]
    [InlineData("identity")]
    [InlineData("ftp://identity.example.com")]
    [InlineData("http://localhost:5001/")]
    [InlineData("http://localhost:5001/identity")]
    [InlineData("http://localhost:5001?x=1")]
    [InlineData("http://localhost:5001#top")]
    [InlineData("http://user:secret@localhost:5001")]
    public void ASetting_ThatIsNotAnOrigin_StopsIdentity_NamingTheSetting(string issuer)
    {
        new IssuerSettings { IssuerUri = issuer }.Invoking(s => s.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"IdentityServer:IssuerUri must be an http:// or https:// address without a path*'{issuer}'.");
    }

    [Fact]
    public void TheSetting_IsReadFromTheIdentityServerSection_AndSharesItWithTheOthers()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityServer:IssuerUri"] = "http://localhost:5001",
            // the gateway and the APIs read their own keys from the same section of a shared configuration
            ["IdentityServer:Authority"] = "http://identity:8080",
        }).Build();

        IssuerSettings.From(configuration).IssuerUri.Should().Be("http://localhost:5001");
        IssuerSettings.From(new ConfigurationBuilder().Build()).IssuerUri.Should().BeEmpty();
    }

    // ---- what it does, on Duende's own discovery endpoint

    private static async Task<string> IssuerSeenAsync(string? configuredIssuer, string requestedAs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddIdentityServer(options =>
            {
                if (configuredIssuer is not null)
                    options.IssuerUri = configuredIssuer;
            })
            .AddInMemoryIdentityResources([new IdentityResources.OpenId()])
            .AddDeveloperSigningCredential(persistKey: false);
        await using var app = builder.Build();
        app.UseIdentityServer();
        await app.StartAsync();

        // the same server asked by the name the gateway uses and by the name a browser uses
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(app.Urls.First()), "/.well-known/openid-configuration"));
        request.Headers.Host = requestedAs;
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("issuer").GetString()!;
    }

    [Fact]
    public async Task WithoutASetting_TheIssuerFollowsTheNameOfTheRequest_SoTheGatewayAndABrowserDisagree()
    {
        (await IssuerSeenAsync(null, "identity:8080")).Should().Be("http://identity:8080");
        (await IssuerSeenAsync(null, "localhost:5001")).Should().Be("http://localhost:5001");
    }

    [Fact]
    public async Task WithTheSetting_TheIssuerIsTheSame_WhicheverNameAskedForIt()
    {
        (await IssuerSeenAsync("http://localhost:5001", "identity:8080")).Should().Be("http://localhost:5001");
        (await IssuerSeenAsync("http://localhost:5001", "localhost:5001")).Should().Be("http://localhost:5001");
    }

    // ---- the places the setting is written down agree with each other

    private const string Default = "http://localhost:5001";

    [Fact]
    public void IdentitysAppsettings_ListTheDefault_WhichIsNoIssuerOfItsOwn()
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read("src/Identity/ShopEasy.Identity", "appsettings.json"));

        document.RootElement.GetProperty("IdentityServer").GetProperty("IssuerUri").GetString()
            .Should().Be(new IssuerSettings().IssuerUri);
    }

    [Fact]
    public void Compose_GivesIdentityItsIssuer_AndTheGatewayTheSameAddress_FromOneVariable()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");
        var identity = CallResilienceConfigurationTests.ServiceBlock(compose, "identity");
        var gateway = CallResilienceConfigurationTests.ServiceBlock(compose, "gateway");

        Regex.Match(identity, @"IdentityServer__IssuerUri:\s*""\$\{IDENTITY_PUBLIC_URL:-([^}]*)\}""").Groups[1].Value.Should().Be(Default);
        Regex.Match(gateway, @"IdentityServer__PublicUrl:\s*""\$\{IDENTITY_PUBLIC_URL:-([^}]*)\}""").Groups[1].Value.Should().Be(Default);
    }

    [Fact]
    public void Kubernetes_GivesIdentityTheAddressTheGatewaySendsBrowsersTo()
    {
        var identity = RepositoryFiles.Read("k8s", "identity", "identity.yaml");
        var gateway = RepositoryFiles.Read("k8s", "gateway", "gateway.yaml");

        var issuer = Regex.Match(identity, @"name: IdentityServer__IssuerUri\s+value: ""([^""]+)""").Groups[1].Value;
        var publicUrl = Regex.Match(gateway, @"name: IdentityServer__PublicUrl\s+value: ""([^""]+)""").Groups[1].Value;

        issuer.Should().NotBeEmpty();
        issuer.Should().Be(publicUrl, "tokens must name the address that browsers use");
        new IssuerSettings { IssuerUri = issuer }.Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Fact]
    public void TheEnvExample_ShowsTheVariableWithItsDefault_AndTheReadmeExplainsTheSetting()
    {
        RepositoryFiles.Read(".env.example").Should().Contain($"# IDENTITY_PUBLIC_URL={Default}\n");

        var readme = RepositoryFiles.Read("README.md");
        readme.Should().Contain("`IdentityServer__IssuerUri`").And.Contain("`IDENTITY_PUBLIC_URL`");
    }
}
