using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// The authorization policies on their own, without a web host: what scope, role and kind of token
// each one asks for.
public class AccessPolicyTests
{
    private static ClaimsPrincipal User(
        IEnumerable<string>? scopes = null, string? role = null, string? subject = "user-1",
        string? clientId = "shopeasy-gateway", string? spaceDelimitedScopes = null)
    {
        var claims = new List<Claim>();
        foreach (var scope in scopes ?? [])
            claims.Add(new Claim(ClaimNames.Scope, scope));
        if (spaceDelimitedScopes is not null) claims.Add(new Claim(ClaimNames.Scope, spaceDelimitedScopes));
        if (role is not null) claims.Add(new Claim(ClaimNames.Role, role));
        if (subject is not null) claims.Add(new Claim(ClaimNames.Subject, subject));
        if (clientId is not null) claims.Add(new Claim(ClaimNames.ClientId, clientId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", nameType: ClaimNames.Name, roleType: ClaimNames.Role));
    }

    private static async Task<bool> Allows(AccessPolicy policy, ClaimsPrincipal user)
    {
        var services = new ServiceCollection().AddLogging().AddApiAuthorization([policy]).BuildServiceProvider();
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy.Name);
        return result.Succeeded;
    }

    [Fact]
    public void ScopesAreRead_FromOneClaimPerScope_AndFromASpaceDelimitedClaim()
    {
        User(scopes: ["orders.read", "orders.write"]).Scopes().Should().BeEquivalentTo("orders.read", "orders.write");
        User(spaceDelimitedScopes: "orders.read orders.write").Scopes().Should().BeEquivalentTo("orders.read", "orders.write");
        User().Scopes().Should().BeEmpty();
    }

    [Fact]
    public void AScope_MustMatchExactly()
    {
        User(scopes: ["orders.read.all", "xorders.read", "ORDERS.READ", "orders"]).HasScope("orders.read").Should().BeFalse();
        User(scopes: ["orders.read"]).HasScope("orders.read").Should().BeTrue();
    }

    [Fact]
    public void ATokenForAUser_IsNotAServiceToken_AndATokenWithoutUserIs()
    {
        User(subject: "user-1", clientId: "shopeasy-gateway").IsServiceToken().Should().BeFalse();
        User(subject: null, clientId: "order-service").IsServiceToken().Should().BeTrue();
        User(subject: null, clientId: null).IsServiceToken().Should().BeFalse("no client is named");
    }

    [Fact]
    public async Task AScopePolicy_NeedsTheScope_AndASignedInCaller()
    {
        var policy = new AccessPolicy("p", ApiScopes.OrdersRead);

        (await Allows(policy, User(scopes: [ApiScopes.OrdersRead]))).Should().BeTrue();
        (await Allows(policy, User(scopes: [ApiScopes.OrdersWrite]))).Should().BeFalse();
        (await Allows(policy, User())).Should().BeFalse();
        (await Allows(policy, new ClaimsPrincipal(new ClaimsIdentity()))).Should().BeFalse("not signed in");
    }

    [Fact]
    public async Task AnAdminPolicy_NeedsTheScopeAndTheRole_NeitherAloneIsEnough()
    {
        var policy = new AccessPolicy("p", ApiScopes.OrdersRead, RequireAdmin: true);

        (await Allows(policy, User(scopes: [ApiScopes.OrdersRead], role: Roles.Admin))).Should().BeTrue();
        (await Allows(policy, User(scopes: [ApiScopes.OrdersRead], role: "customer"))).Should().BeFalse();
        (await Allows(policy, User(scopes: [ApiScopes.OrdersRead]))).Should().BeFalse();
        (await Allows(policy, User(scopes: [], role: Roles.Admin))).Should().BeFalse();
    }

    [Fact]
    public async Task AServicePolicy_NeedsTheScopeOnATokenWithoutAUser()
    {
        var policy = new AccessPolicy("p", ApiScopes.InventoryRead, RequireServiceToken: true);

        (await Allows(policy, User(scopes: [ApiScopes.InventoryRead], subject: null, clientId: "order-service"))).Should().BeTrue();
        (await Allows(policy, User(scopes: [ApiScopes.InventoryRead], subject: "user-1", clientId: "shopeasy-gateway", role: Roles.Admin)))
            .Should().BeFalse("a user's token is not a service's token, whatever it carries");
        (await Allows(policy, User(scopes: [], subject: null, clientId: "order-service"))).Should().BeFalse();
    }

    [Fact]
    public async Task EveryEndpointWithoutAPolicy_RequiresASignedInCaller()
    {
        var services = new ServiceCollection().AddLogging().AddApiAuthorization([]).BuildServiceProvider();
        var fallback = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        fallback.Should().NotBeNull();
        fallback!.Requirements.Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement);
    }

    // ---- the lists

    private static IEnumerable<(string Api, AccessPolicy Policy)> AllPolicies() =>
        AccessPolicies.Product.Select(p => ("Product", p))
            .Concat(AccessPolicies.Order.Select(p => ("Order", p)))
            .Concat(AccessPolicies.Inventory.Select(p => ("Inventory", p)))
            .Concat(AccessPolicies.Payment.Select(p => ("Payment", p)));

    [Fact]
    public void PolicyNames_AreUnique_AndEveryConstantHasAPolicy()
    {
        var names = AllPolicies().Select(p => p.Policy.Name).ToList();
        var constants = typeof(PolicyNames).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().BeEquivalentTo(constants, "every name used in an [Authorize] attribute is registered by one API, and vice versa");
    }

    [Fact]
    public void EveryPolicyNameSaysWhatItNeeds()
    {
        foreach (var (_, policy) in AllPolicies())
        {
            var expected = policy.Scope
                + (policy.RequireAdmin ? ":admin" : "")
                + (policy.RequireServiceToken ? ":service" : "");
            policy.Name.Should().Be(expected);
        }
    }

    [Fact]
    public void TheScopeLists_AreConsistent()
    {
        ApiScopes.Web.Should().OnlyHaveUniqueItems();
        ApiScopes.Mobile.Should().BeSubsetOf(ApiScopes.Web, "the mobile app gets less than the web client, never more");
        ApiScopes.OrderService.Should().BeEquivalentTo([ApiScopes.InventoryRead]);
        ApiScopes.Web.Should().BeEquivalentTo(
            typeof(ApiScopes).GetFields().Select(f => (string)f.GetRawConstantValue()!),
            "the web client may ask for every scope there is");
    }
}
