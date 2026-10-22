using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The authorization middleware has decided; this is what the gateway then does. A call under /api
// without a session gets 401, a page gets the sign-in redirect (the authentication service is
// asked to challenge), and everything else is as before.
public class ApiAuthorizationResultHandlerTests
{
    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    private sealed class Outcome
    {
        public int Challenges;
        public int Forbids;
        public bool NextCalled;
        public required HttpContext Http;
        public required MemoryStream Body;
    }

    private static async Task<Outcome> HandleAsync(string path, PolicyAuthorizationResult result)
    {
        var authentication = new RecordingAuthenticationService();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(authentication)
                .BuildServiceProvider()
        };
        http.Request.Path = path;
        var body = new MemoryStream();
        http.Response.Body = body;

        var outcome = new Outcome { Http = http, Body = body };
        await new ApiAuthorizationResultHandler().HandleAsync(
            _ => { outcome.NextCalled = true; return Task.CompletedTask; }, http, Policy, result);

        outcome.Challenges = authentication.Challenges;
        outcome.Forbids = authentication.Forbids;
        return outcome;
    }

    [Theory]
    [InlineData("/api/orders")]
    [InlineData("/api/v1/products/7")]
    [InlineData("/API/Orders")]
    public async Task ACallUnderApi_WithoutASession_GetsA401_AndNoSignInIsStarted(string path)
    {
        var outcome = await HandleAsync(path, PolicyAuthorizationResult.Challenge());

        outcome.Http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        outcome.Http.Response.ContentType.Should().StartWith("application/json");
        Encoding.UTF8.GetString(outcome.Body.ToArray()).Should().Contain("not_signed_in");
        outcome.Challenges.Should().Be(0, "a sign-in would set a correlation cookie and redirect");
        outcome.NextCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("/sagas")]
    [InlineData("/account/token")]
    [InlineData("/apiary")]
    public async Task APage_WithoutASession_IsChallenged_ThatIsSentToSignIn(string path)
    {
        var outcome = await HandleAsync(path, PolicyAuthorizationResult.Challenge());

        outcome.Challenges.Should().Be(1);
        outcome.Body.Length.Should().Be(0);
        outcome.NextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ACallUnderApi_ByAUserWhoMayNot_IsStillForbidden()
    {
        var outcome = await HandleAsync("/api/inventory", PolicyAuthorizationResult.Forbid());

        outcome.Forbids.Should().Be(1);
        outcome.Challenges.Should().Be(0);
        outcome.Http.Response.StatusCode.Should().NotBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ACallUnderApi_ThatIsAllowed_GoesOn()
    {
        var outcome = await HandleAsync("/api/orders", PolicyAuthorizationResult.Success());

        outcome.NextCalled.Should().BeTrue();
        outcome.Body.Length.Should().Be(0);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public int Challenges;
        public int Forbids;

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Challenges++;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Forbids++;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }
}
