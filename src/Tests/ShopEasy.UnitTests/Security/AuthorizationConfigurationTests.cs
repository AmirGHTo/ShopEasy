using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// The settings for token validation and browser-facing security are written down in
// appsettings.json, the Compose file with .env.example, and the Kubernetes ConfigMap. The code's
// defaults are the truth; these tests fail when one of the copies says something else.
public class AuthorizationConfigurationTests
{
    private static readonly ApiAuthenticationOptions Authentication = new();
    private static readonly WebSecurityOptions Cookies = new();
    private static readonly ForwardedHeadersSettings Forwarded = new();

    public static readonly string[] Apis =
    [
        "src/Services/ShopEasy.ProductService", "src/Services/ShopEasy.InventoryService",
        "src/Services/ShopEasy.OrderService", "src/Services/ShopEasy.PaymentService", "src/Gateway/ShopEasy.Gateway",
    ];

    public static readonly string[] BrowserFacing = ["src/Gateway/ShopEasy.Gateway", "src/Identity/ShopEasy.Identity"];

    public static TheoryData<string> ApiDirectories() => new(Apis);
    public static TheoryData<string> BrowserDirectories() => new(BrowserFacing);

    private static JsonElement Settings(string directory)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read(directory, "appsettings.json"));
        return document.RootElement.Clone();
    }

    private static string ServiceBlock(string compose, string service)
    {
        var match = Regex.Match(compose, $@"^  {service}:\n(.*?)(?=^  [a-z][a-z-]*:\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the Compose file should define {service}");
        return match.Value;
    }

    [Theory]
    [MemberData(nameof(ApiDirectories))]
    public void EveryTokenValidatingServicesAppsettings_ListTheAuthenticationDefaults(string directory)
    {
        var section = Settings(directory).GetProperty("IdentityServer");

        section.GetProperty("Authority").GetString().Should().Be(Authentication.Authority);
        section.GetProperty("RequireHttpsMetadata").GetBoolean().Should().Be(Authentication.RequireHttpsMetadata);
    }

    [Theory]
    [MemberData(nameof(BrowserDirectories))]
    public void TheBrowserFacingServicesAppsettings_ListTheCookieAndForwardedHeaderDefaults(string directory)
    {
        var settings = Settings(directory);

        settings.GetProperty("Security").GetProperty("CookieSecurePolicy").GetString()
            .Should().Be(Cookies.CookieSecurePolicy.ToString());
        var forwarded = settings.GetProperty("ForwardedHeaders");
        forwarded.GetProperty("Enabled").GetBoolean().Should().Be(Forwarded.Enabled);
        forwarded.GetProperty("KnownNetworks").GetString().Should().Be(Forwarded.KnownNetworks);
        forwarded.GetProperty("KnownProxies").GetString().Should().Be(Forwarded.KnownProxies);
    }

    [Fact]
    public void Compose_PassesTheSettingsWithTheDefaults()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        compose.Should().Contain($"IdentityServer__RequireHttpsMetadata: \"${{IDENTITY_REQUIRE_HTTPS_METADATA:-{Authentication.RequireHttpsMetadata.ToString().ToLowerInvariant()}}}\"");
        compose.Should().Contain($"Security__CookieSecurePolicy: \"${{SECURITY_COOKIE_SECURE_POLICY:-{Cookies.CookieSecurePolicy}}}\"");
        compose.Should().Contain($"ForwardedHeaders__Enabled: \"${{FORWARDED_HEADERS_ENABLED:-{Forwarded.Enabled.ToString().ToLowerInvariant()}}}\"");
        compose.Should().Contain("ForwardedHeaders__KnownNetworks: \"${FORWARDED_HEADERS_KNOWN_NETWORKS:-}\"");
        compose.Should().Contain("ForwardedHeaders__KnownProxies: \"${FORWARDED_HEADERS_KNOWN_PROXIES:-}\"");
    }

    [Fact]
    public void Compose_GivesTheSettingsToTheServicesThatUseThem()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        foreach (var service in new[] { "product", "inventory", "order", "payment", "gateway" })
            ServiceBlock(compose, service).Should().Contain("*identity-validation", $"{service} validates tokens");
        foreach (var service in new[] { "gateway", "identity" })
            ServiceBlock(compose, service).Should().Contain("*web-security", $"a browser talks to {service}");
        ServiceBlock(compose, "identity").Should().NotContain("*identity-validation");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasTheSettingsWithTheDefaults()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        configMap.Should().Contain($"IdentityServer__RequireHttpsMetadata: \"{Authentication.RequireHttpsMetadata.ToString().ToLowerInvariant()}\"");
        configMap.Should().Contain($"Security__CookieSecurePolicy: \"{Cookies.CookieSecurePolicy}\"");
        configMap.Should().Contain($"ForwardedHeaders__Enabled: \"{Forwarded.Enabled.ToString().ToLowerInvariant()}\"");
        configMap.Should().Contain("ForwardedHeaders__KnownNetworks: \"\"");
        configMap.Should().Contain("ForwardedHeaders__KnownProxies: \"\"");
    }

    [Fact]
    public void TheEnvExample_ShowsTheVariablesWithTheDefaults()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().Contain($"# IDENTITY_REQUIRE_HTTPS_METADATA={Authentication.RequireHttpsMetadata.ToString().ToLowerInvariant()}");
        example.Should().Contain($"# SECURITY_COOKIE_SECURE_POLICY={Cookies.CookieSecurePolicy}");
        example.Should().Contain($"# FORWARDED_HEADERS_ENABLED={Forwarded.Enabled.ToString().ToLowerInvariant()}");
        example.Should().MatchRegex(@"(?m)^# FORWARDED_HEADERS_KNOWN_NETWORKS=$");
        example.Should().MatchRegex(@"(?m)^# FORWARDED_HEADERS_KNOWN_PROXIES=$");
    }

    [Fact]
    public void TheServiceTokenSettings_AreInTheOrderServicesConfiguration()
    {
        var order = Settings("src/Services/ShopEasy.OrderService");
        var compose = RepositoryFiles.Read("docker-compose.yml");

        order.GetProperty("ServiceAuth").GetProperty("ClientId").GetString().Should().Be("order-service");
        ServiceBlock(compose, "order").Should().Contain("ServiceAuth__ClientId: \"order-service\"")
            .And.Contain("ServiceAuth__ClientSecret: ${ORDER_SERVICE_CLIENT_SECRET:?");
        ServiceBlock(compose, "identity").Should().Contain("Clients__OrderService__Secret: ${ORDER_SERVICE_CLIENT_SECRET:?");
    }
}
