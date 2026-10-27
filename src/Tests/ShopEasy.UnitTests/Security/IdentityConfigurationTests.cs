using Duende.IdentityServer.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ShopEasy.Identity.Configuration;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// What Identity issues tokens for: one API resource (audience) per API, the scopes that belong to
// it, and for each client only the scopes it needs.
public class IdentityConfigurationTests
{
    private static List<Client> Clients() => IdentityServerConfig.GetClients(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clients:Gateway:Secret"] = "gateway-secret",
            ["Clients:OrderService:Secret"] = "order-secret",
        }).Build()).ToList();

    private static Client Client(string id) => Clients().Single(c => c.ClientId == id);

    [Fact]
    public void ThereIsOneApiResourcePerApi_NamedLikeTheAudienceTheServicesCheck()
    {
        IdentityServerConfig.ApiResources.Select(r => r.Name).Should().BeEquivalentTo(ApiAudiences.All);
    }

    [Fact]
    public void EveryScope_BelongsToExactlyOneApi()
    {
        var scopes = IdentityServerConfig.ApiScopes.Select(s => s.Name).ToList();
        var inResources = IdentityServerConfig.ApiResources.SelectMany(r => r.Scopes).ToList();

        inResources.Should().OnlyHaveUniqueItems("a scope in two APIs would put both audiences in its tokens");
        inResources.Should().BeEquivalentTo(scopes);
        scopes.Should().BeEquivalentTo(
            typeof(ApiScopes).GetFields().Select(f => (string)f.GetRawConstantValue()!),
            "the scopes Identity knows are the ones the services demand");
    }

    [Theory]
    [InlineData(ApiAudiences.Product)]
    [InlineData(ApiAudiences.Order)]
    [InlineData(ApiAudiences.Inventory)]
    [InlineData(ApiAudiences.Payment)]
    public void EveryScopeAnApiDemands_IsOneOfItsOwnResourceScopes(string audience)
    {
        var policies = audience switch
        {
            ApiAudiences.Product => AccessPolicies.Product,
            ApiAudiences.Order => AccessPolicies.Order,
            ApiAudiences.Inventory => AccessPolicies.Inventory,
            _ => AccessPolicies.Payment,
        };
        var resourceScopes = IdentityServerConfig.ApiResources.Single(r => r.Name == audience).Scopes;

        policies.Select(p => p.Scope).Distinct().Should().BeSubsetOf(resourceScopes,
            "a token for the API has to be able to carry the scopes its endpoints ask for");
    }

    [Fact]
    public void TheWebGateway_MayAskForEveryApiScope_AndNothingElse()
    {
        var allowed = Client("shopeasy-gateway").AllowedScopes;

        allowed.Should().BeEquivalentTo(["openid", "profile", "email", .. ApiScopes.Web]);
        allowed.Should().NotContain("offline_access", "refresh tokens are the AllowOfflineAccess flag, not a scope entry");
    }

    [Fact]
    public void TheMobileApp_GetsNoAdministrationScopes()
    {
        var mobile = Client("shopeasy-mobile");

        mobile.AllowedScopes.Should().BeEquivalentTo(["openid", "profile", "email", .. ApiScopes.Mobile]);
        mobile.AllowedScopes.Should().NotContain([ApiScopes.ProductsWrite, ApiScopes.InventoryWrite]);
        mobile.RequireClientSecret.Should().BeFalse("a public client has no secret");
        mobile.RequirePkce.Should().BeTrue();
    }

    [Fact]
    public void TheOrderService_IsAMachineClient_WithOnlyTheScopesItNeeds()
    {
        var service = Client("order-service");

        service.AllowedGrantTypes.Should().BeEquivalentTo(GrantTypes.ClientCredentials);
        service.AllowedScopes.Should().BeEquivalentTo([ApiScopes.InventoryRead, ApiScopes.PaymentRead]);
        service.AllowOfflineAccess.Should().BeFalse();
        service.AccessTokenLifetime.Should().BeLessThanOrEqualTo(900, "service tokens are short-lived");
    }

    [Fact]
    public void OnlyTheOrderService_CanUseClientCredentials()
    {
        Clients().Where(c => c.AllowedGrantTypes.Contains(GrantType.ClientCredentials))
            .Select(c => c.ClientId).Should().BeEquivalentTo(["order-service"]);
    }

    [Fact]
    public void NoClient_CanGetTheScopesOfAnApiItDoesNotUse()
    {
        // the order service reads stock levels and what Payment holds, and nothing else
        var allScopes = typeof(ApiScopes).GetFields().Select(f => (string)f.GetRawConstantValue()!);
        Client("order-service").AllowedScopes.Should().NotIntersectWith(
            allScopes.Where(s => s != ApiScopes.InventoryRead && s != ApiScopes.PaymentRead));
    }
}
