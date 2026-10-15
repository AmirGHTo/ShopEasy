using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// Bearer-token validation as every API does it, with real signed tokens: a token is accepted only
// when Identity signed it, it has not expired, it was issued by Identity and it is meant for this API.
public class ApiAuthenticationTests : IAsyncLifetime
{
    private AuthHost _api = null!;

    public async Task InitializeAsync() => _api = await AuthHost.StartAsync(
        ApiAudiences.Order,
        AccessPolicies.Order,
        map: app =>
        {
            app.MapGet("/whoami", (ClaimsPrincipal user) => Results.Ok(new
            {
                subject = user.FindFirst("sub")?.Value,
                isAdmin = user.IsInRole(Roles.Admin),
                scopes = user.Scopes().ToArray(),
                service = user.IsServiceToken(),
            }));
            app.MapGet("/orders-read", () => "ok").RequireAuthorization(PolicyNames.OrdersRead);
            app.MapGet("/public", () => "ok").AllowAnonymous();
            app.MapGet("/unmarked", () => "ok"); // no attribute, no policy: the fallback decides
        });

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private static TestIdentityProvider.Token Valid() => new(
        [ApiAudiences.Order], [ApiScopes.OrdersRead], Subject: "user-1", ClientId: "shopeasy-gateway", Roles: ["customer"]);

    private Task<HttpStatusCode> Get(string path, string? token) => _api.SendAsync(HttpMethod.Get, path, token);

    [Fact]
    public async Task AValidToken_IsAccepted_AndItsClaimsKeepTheNamesIdentityIssuedThem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Valid().Issue());

        using var response = await _api.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"subject\":\"user-1\"").And.Contain("\"isAdmin\":false").And.Contain("orders.read");
    }

    [Fact]
    public async Task TheRoleClaim_MakesIsInRoleWork()
    {
        var admin = Valid() with { Roles = [Roles.Admin] };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Issue());

        using var response = await _api.Client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Contain("\"isAdmin\":true");
    }

    [Fact]
    public async Task ATokenWithSeveralAudiences_IsAcceptedWhenOneOfThemIsThisApi()
    {
        var token = Valid() with { Audiences = ["http://identity.test/resources", ApiAudiences.Inventory, ApiAudiences.Order] };

        (await Get("/orders-read", token.Issue())).Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> BadTokens()
    {
        var valid = new TestIdentityProvider.Token([ApiAudiences.Order], [ApiScopes.OrdersRead], Subject: "user-1", Roles: [Roles.Admin]);
        return new TheoryData<string>
        {
            // meant for another API
            (valid with { Audiences = [ApiAudiences.Inventory] }).Issue(),
            // only the audience Identity adds for its own resources
            (valid with { Audiences = ["http://identity.test/resources"] }).Issue(),
            // no audience at all
            (valid with { Audiences = [] }).Issue(),
            // expired an hour and a half ago (more than the allowed clock difference)
            (valid with { Age = TimeSpan.FromMinutes(90) }).Issue(),
            // signed by somebody else
            (valid with { SignedByAnotherKey = true }).Issue(),
            // issued by somebody else
            (valid with { Issuer = "http://evil.test" }).Issue(),
            // not signed at all
            TestIdentityProvider.Unsigned([ApiAudiences.Order], [ApiScopes.OrdersRead]),
            "not-a-token",
            "a.b.c",
        };
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task ATokenThatIsNotForThisApiOrNotGenuine_IsRejected(string token) =>
        (await Get("/orders-read", token)).Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task ANonBearerAuthorizationHeader_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders-read");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("user:password"u8.ToArray()));

        using var response = await _api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnEndpointWithNoAttributeAtAll_RequiresASignedInCaller()
    {
        (await Get("/unmarked", null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/unmarked", Valid().Issue())).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnEndpointMarkedAnonymous_NeedsNoToken_AndIgnoresABadOne()
    {
        (await Get("/public", null)).Should().Be(HttpStatusCode.OK);
        (await Get("/public", "not-a-token")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APolicy_ChecksTheScopeAfterTheToken()
    {
        var withoutScope = Valid() with { Scopes = ["openid"] };

        (await Get("/orders-read", withoutScope.Issue())).Should().Be(HttpStatusCode.Forbidden);
        (await Get("/orders-read", Valid().Issue())).Should().Be(HttpStatusCode.OK);
    }

    // ---- the settings

    [Fact]
    public void TheAuthority_MustBeAnHttpAddress()
    {
        new ApiAuthenticationOptions { Authority = "http://identity:8080" }.Invoking(o => o.Validate()).Should().NotThrow();
        new ApiAuthenticationOptions { Authority = "https://login.example.com" }.Invoking(o => o.Validate()).Should().NotThrow();
        new ApiAuthenticationOptions { Authority = "identity" }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
        new ApiAuthenticationOptions { Authority = "ftp://identity" }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RequiringHttpsMetadata_NeedsAnHttpsAuthority()
    {
        new ApiAuthenticationOptions { Authority = "https://login.example.com", RequireHttpsMetadata = true }
            .Invoking(o => o.Validate()).Should().NotThrow();
        new ApiAuthenticationOptions { Authority = "http://identity:8080", RequireHttpsMetadata = true }
            .Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*RequireHttpsMetadata*");
    }

    [Fact]
    public void TheDefaults_AreTheInternalAuthority_WithoutTheHttpsRequirement()
    {
        var defaults = new ApiAuthenticationOptions();

        defaults.Authority.Should().Be("http://identity:8080");
        defaults.RequireHttpsMetadata.Should().BeFalse();
        defaults.Invoking(o => o.Validate()).Should().NotThrow();
    }
}
