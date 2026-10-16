using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// The gateway keeps the user's tokens in the session cookie and forwards the access token, which
// lives an hour, while the session lasts up to thirty days. These tests cover renewing it.
public class AccessTokenRenewalTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- the exchange with Identity

    private sealed class FakeIdentity : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = null!;
        public int Requests;
        public Dictionary<string, string> LastForm = new();
        public Uri? LastUri;

        public FakeIdentity() => Respond = _ => Task.FromResult(Tokens("new-access", "new-refresh"));

        public static HttpResponseMessage Tokens(string access, string? refresh, int expiresIn = 3600) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"access_token":"{{access}}",{{(refresh is null ? "" : $"\"refresh_token\":\"{refresh}\",")}}"expires_in":{{expiresIn}},"token_type":"Bearer"}""",
                Encoding.UTF8, "application/json")
        };

        public static HttpResponseMessage Error(HttpStatusCode status, string error) =>
            new(status) { Content = new StringContent($$"""{"error":"{{error}}"}""", Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            LastUri = request.RequestUri;
            LastForm = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(pair => pair.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            return await Respond(request);
        }
    }

    private static (AccessTokenRefresher Refresher, FakeIdentity Identity) Refresher(
        TimeSpan? keepAnswer = null, TimeSpan? keepFailure = null)
    {
        var identity = new FakeIdentity();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AccessTokenRefresher.HttpClientName).Returns(_ => new HttpClient(identity));

        var options = new OpenIdConnectOptions
        {
            ClientId = "shopeasy-gateway",
            ClientSecret = "gateway-secret",
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { TokenEndpoint = "http://identity.test/connect/token" }),
        };
        var monitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Get(AccessTokenRefresher.Scheme).Returns(options);

        return (new AccessTokenRefresher(factory, monitor, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AccessTokenRefresher>.Instance, new FixedClock(), keepAnswer, keepFailure), identity);
    }

    [Fact]
    public async Task ARefreshToken_IsExchangedForNewTokens_AtTheTokenEndpointOfIdentity()
    {
        var (refresher, identity) = Refresher();

        var result = await refresher.RefreshAsync("old-refresh", CancellationToken.None);

        result.Outcome.Should().Be(RenewalOutcome.Renewed);
        result.AccessToken.Should().Be("new-access");
        result.RefreshToken.Should().Be("new-refresh");
        result.ExpiresAt.Should().Be(Now.AddHours(1));
        identity.LastUri.Should().Be(new Uri("http://identity.test/connect/token"));
        identity.LastForm.Should().Contain("grant_type", "refresh_token").And.Contain("refresh_token", "old-refresh")
            .And.Contain("client_id", "shopeasy-gateway").And.Contain("client_secret", "gateway-secret");
    }

    [Fact]
    public async Task ARefreshTokenIdentityNoLongerAccepts_IsRejected()
    {
        var (refresher, identity) = Refresher();
        identity.Respond = _ => Task.FromResult(FakeIdentity.Error(HttpStatusCode.BadRequest, "invalid_grant"));

        (await refresher.RefreshAsync("revoked", CancellationToken.None)).Outcome.Should().Be(RenewalOutcome.Rejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "server_error")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_client")] // our own misconfiguration, not the user's session
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task AnythingElseIdentityAnswers_LeavesTheSessionAlone(HttpStatusCode status, string error)
    {
        var (refresher, identity) = Refresher();
        identity.Respond = _ => Task.FromResult(FakeIdentity.Error(status, error));

        (await refresher.RefreshAsync("token", CancellationToken.None)).Outcome.Should().Be(RenewalOutcome.Unavailable);
    }

    [Fact]
    public async Task AnIdentityThatCannotBeReached_IsNotAskedAgainRightAway_ButAfterAWhile()
    {
        var (refresher, identity) = Refresher(keepFailure: TimeSpan.FromMilliseconds(300));
        identity.Respond = _ => throw new HttpRequestException("connection refused");

        (await refresher.RefreshAsync("token", CancellationToken.None)).Outcome.Should().Be(RenewalOutcome.Unavailable);
        (await refresher.RefreshAsync("token", CancellationToken.None)).Outcome.Should().Be(RenewalOutcome.Unavailable);
        identity.Requests.Should().Be(1, "the second request did not wait for Identity again");

        await Task.Delay(500);
        identity.Respond = _ => Task.FromResult(FakeIdentity.Tokens("new-access", null));

        (await refresher.RefreshAsync("token", CancellationToken.None)).Outcome.Should().Be(RenewalOutcome.Renewed);
        identity.Requests.Should().Be(2);
    }

    [Fact]
    public async Task TheWaitingPeriodAfterAFailure_DoesNotGrowWhileRequestsKeepArriving()
    {
        var (refresher, identity) = Refresher(keepFailure: TimeSpan.FromMilliseconds(400));
        identity.Respond = _ => throw new HttpRequestException("connection refused");
        await refresher.RefreshAsync("token", CancellationToken.None);

        // requests arriving more often than the waiting period must not keep it from ending
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(100);
            await refresher.RefreshAsync("token", CancellationToken.None);
        }

        identity.Requests.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task RequestsOfOneSession_ShareOneExchange_AndItsAnswerIsReusedBriefly()
    {
        var (refresher, identity) = Refresher();
        identity.Respond = async _ => { await Task.Delay(100); return FakeIdentity.Tokens("new-access", "new-refresh"); };

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => refresher.RefreshAsync("same", CancellationToken.None)));
        var later = await refresher.RefreshAsync("same", CancellationToken.None); // the old cookie again, a moment later

        results.Should().OnlyContain(r => r.AccessToken == "new-access");
        later.AccessToken.Should().Be("new-access");
        identity.Requests.Should().Be(1);
    }

    [Fact]
    public async Task DifferentSessions_AreExchangedSeparately()
    {
        var (refresher, identity) = Refresher();

        await refresher.RefreshAsync("session-1", CancellationToken.None);
        await refresher.RefreshAsync("session-2", CancellationToken.None);

        identity.Requests.Should().Be(2);
    }

    // ---- what happens to the session cookie

    private static (CookieValidatePrincipalContext Context, IAccessTokenRefresher Refresher, IAuthenticationService Auth) Session(
        string? expiresAt, string? refreshToken = "old-refresh")
    {
        var refresher = Substitute.For<IAccessTokenRefresher>();
        var auth = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection()
            .AddSingleton(refresher).AddSingleton(auth).AddSingleton<TimeProvider>(new FixedClock())
            .BuildServiceProvider();

        var properties = new AuthenticationProperties();
        var tokens = new List<AuthenticationToken> { new() { Name = "access_token", Value = "old-access" } };
        if (refreshToken is not null) tokens.Add(new AuthenticationToken { Name = "refresh_token", Value = refreshToken });
        if (expiresAt is not null) tokens.Add(new AuthenticationToken { Name = "expires_at", Value = expiresAt });
        properties.StoreTokens(tokens);

        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), properties, scheme.Name);
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services }, scheme, new CookieAuthenticationOptions(), ticket);
        return (context, refresher, auth);
    }

    private static string In(TimeSpan span) => Now.Add(span).ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AnAccessTokenWithTimeLeft_IsLeftAlone()
    {
        var (context, refresher, _) = Session(In(TimeSpan.FromMinutes(30)));

        await AccessTokenRenewal.ValidateAsync(context);

        await refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        context.ShouldRenew.Should().BeFalse();
        context.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task AnAccessTokenAboutToExpire_IsRenewed_AndTheCookieRewritten()
    {
        var (context, refresher, _) = Session(In(TimeSpan.FromSeconds(30)));
        var expiresAt = Now.AddHours(1);
        refresher.RefreshAsync("old-refresh", Arg.Any<CancellationToken>())
            .Returns(new RenewalResult(RenewalOutcome.Renewed, "new-access", "new-refresh", expiresAt));

        await AccessTokenRenewal.ValidateAsync(context);

        context.Properties.GetTokenValue("access_token").Should().Be("new-access");
        context.Properties.GetTokenValue("refresh_token").Should().Be("new-refresh");
        context.Properties.GetTokenValue("expires_at").Should().Be(expiresAt.ToString("o", CultureInfo.InvariantCulture));
        context.ShouldRenew.Should().BeTrue();
        context.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task AnExpiredAccessToken_IsRenewedToo()
    {
        var (context, refresher, _) = Session(In(TimeSpan.FromHours(-2)));
        refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RenewalResult(RenewalOutcome.Renewed, "new-access", null, Now.AddHours(1)));

        await AccessTokenRenewal.ValidateAsync(context);

        context.Properties.GetTokenValue("access_token").Should().Be("new-access");
        context.Properties.GetTokenValue("refresh_token").Should().Be("old-refresh", "Identity sent no new one: the old one stays");
    }

    [Fact]
    public async Task WhenIdentityNoLongerAcceptsTheSession_TheUserIsSignedOut()
    {
        var (context, refresher, auth) = Session(In(TimeSpan.FromSeconds(10)));
        refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new RenewalResult(RenewalOutcome.Rejected));

        await AccessTokenRenewal.ValidateAsync(context);

        context.Principal.Should().BeNull("the session is no longer valid, the next request is a login");
        await auth.Received(1).SignOutAsync(Arg.Any<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task WhenIdentityCannotBeReached_TheSessionAndItsOldTokenAreKept()
    {
        var (context, refresher, auth) = Session(In(TimeSpan.FromSeconds(10)));
        refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new RenewalResult(RenewalOutcome.Unavailable));

        await AccessTokenRenewal.ValidateAsync(context);

        context.Principal.Should().NotBeNull();
        context.ShouldRenew.Should().BeFalse();
        context.Properties.GetTokenValue("access_token").Should().Be("old-access");
        await auth.DidNotReceive().SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task ASessionWithoutARefreshToken_EndsWhenItsAccessTokenDoes()
    {
        var expired = Session(In(TimeSpan.FromMinutes(-1)), refreshToken: null);
        await AccessTokenRenewal.ValidateAsync(expired.Context);
        expired.Context.Principal.Should().BeNull();

        var valid = Session(In(TimeSpan.FromSeconds(30)), refreshToken: null);
        await AccessTokenRenewal.ValidateAsync(valid.Context);
        valid.Context.Principal.Should().NotBeNull("it still has some time left");
    }

    [Fact]
    public async Task ASessionWithoutTokens_IsNotTouched()
    {
        var (context, refresher, _) = Session(expiresAt: null);

        await AccessTokenRenewal.ValidateAsync(context);

        await refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        context.Principal.Should().NotBeNull();
    }
}
