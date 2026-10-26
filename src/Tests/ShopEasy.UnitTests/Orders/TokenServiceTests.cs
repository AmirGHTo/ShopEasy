using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The token the Order service uses for itself: what it asks Identity for, how long it keeps it, and
// what it does when Identity does not answer.
public class TokenServiceTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Identity's token endpoint, answering from a script
    private sealed class FakeIdentity : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = null!;
        public int Requests;
        public Uri? LastUri;
        public Dictionary<string, string> LastForm = new();
        private int _issued;

        public FakeIdentity() => Respond = (_, _) => Task.FromResult(Token(900));

        public HttpResponseMessage Token(int expiresIn) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"access_token":"token-{{Interlocked.Increment(ref _issued)}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""", Encoding.UTF8, "application/json")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            LastUri = request.RequestUri;
            LastForm = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(pair => pair.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            return await Respond(request, ct);
        }
    }

    private static (TokenService Service, FakeIdentity Identity, ManualClock Clock) Create(Dictionary<string, string?>? settings = null)
    {
        var identity = new FakeIdentity();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("idp").Returns(_ => new HttpClient(identity));
        var clock = new ManualClock();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>
        {
            ["IdentityServer:Authority"] = "http://identity.test/",
            ["ServiceAuth:ClientSecret"] = "the-secret",
        }).Build();
        return (new TokenService(factory, config, NullLogger<TokenService>.Instance, clock), identity, clock);
    }

    [Fact]
    public async Task AsksForTheClientCredentialsGrant_WithOnlyTheScopeOrderNeeds()
    {
        var (service, identity, _) = Create();

        var token = await service.GetServiceTokenAsync();

        token.Should().Be("token-1");
        identity.LastUri.Should().Be(new Uri("http://identity.test/connect/token"));
        identity.LastForm["grant_type"].Should().Be("client_credentials");
        identity.LastForm["client_id"].Should().Be("order-service");
        identity.LastForm["client_secret"].Should().Be("the-secret");
        identity.LastForm["scope"].Should().Be("inventory.read", "nothing else is asked for: no stock changes, no payments");
    }

    [Fact]
    public async Task ATokenServiceForPayment_AsksForThePaymentScopeOnly_AndKeepsItsOwnToken()
    {
        var (inventory, identity, _) = Create();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("idp").Returns(_ => new HttpClient(identity));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityServer:Authority"] = "http://identity.test/",
            ["ServiceAuth:ClientSecret"] = "the-secret",
        }).Build();
        var payment = new TokenService(factory, config, NullLogger<TokenService>.Instance, scope: "payment.read");

        var forInventory = await inventory.GetServiceTokenAsync();
        var forPayment = await payment.GetServiceTokenAsync();

        identity.LastForm["scope"].Should().Be("payment.read", "one scope per token, so the token names one API");
        forPayment.Should().NotBe(forInventory, "each API has its own token, neither replaces the other");
        (await inventory.GetServiceTokenAsync()).Should().Be(forInventory);
    }

    [Fact]
    public async Task KeepsTheToken_UntilItIsAboutToExpire()
    {
        var (service, identity, clock) = Create();

        var first = await service.GetServiceTokenAsync();
        clock.Advance(TimeSpan.FromSeconds(839)); // 61 s left
        var second = await service.GetServiceTokenAsync();
        clock.Advance(TimeSpan.FromSeconds(2));   // 59 s left: due for replacement
        var third = await service.GetServiceTokenAsync();

        second.Should().Be(first);
        third.Should().NotBe(first);
        identity.Requests.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneRequestToIdentity()
    {
        var (service, identity, _) = Create();
        identity.Respond = async (_, ct) => { await Task.Delay(100, ct); return identity.Token(900); };

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetServiceTokenAsync()));

        tokens.Distinct().Should().ContainSingle();
        identity.Requests.Should().Be(1);
    }

    [Fact]
    public async Task WhenIdentityIsDown_AndThereIsNoToken_TheCallFails()
    {
        var (service, identity, _) = Create();
        identity.Respond = (_, _) => throw new HttpRequestException("connection refused");

        await service.Invoking(s => s.GetServiceTokenAsync())
            .Should().ThrowAsync<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task AfterAFailure_IdentityIsLeftAloneForAWhile_ThenAskedAgain()
    {
        var (service, identity, clock) = Create();
        identity.Respond = (_, _) => throw new HttpRequestException("connection refused");

        await service.Invoking(s => s.GetServiceTokenAsync()).Should().ThrowAsync<ServiceTokenUnavailableException>();
        await service.Invoking(s => s.GetServiceTokenAsync()).Should().ThrowAsync<ServiceTokenUnavailableException>();
        identity.Requests.Should().Be(1, "the second call failed at once instead of waiting for Identity again");

        clock.Advance(TokenService.RetryAfterFailure + TimeSpan.FromSeconds(1));
        identity.Respond = (_, _) => Task.FromResult(identity.Token(900));

        (await service.GetServiceTokenAsync()).Should().StartWith("token-");
        identity.Requests.Should().Be(2);
    }

    [Fact]
    public async Task WhenTheTokenIsDueForReplacementButIdentityIsDown_TheOldOneKeepsWorkingUntilItExpires()
    {
        var (service, identity, clock) = Create();
        var original = await service.GetServiceTokenAsync();
        identity.Respond = (_, _) => throw new HttpRequestException("connection refused");

        clock.Advance(TimeSpan.FromSeconds(850));   // 50 s left, replacement is due
        (await service.GetServiceTokenAsync()).Should().Be(original);

        clock.Advance(TimeSpan.FromSeconds(46));    // 4 s left: too close to rely on
        await service.Invoking(s => s.GetServiceTokenAsync()).Should().ThrowAsync<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task ARejectedToken_IsForgotten_ButANewerOneIsNot()
    {
        var (service, identity, _) = Create();
        var first = await service.GetServiceTokenAsync();

        service.Invalidate("some-other-token");
        (await service.GetServiceTokenAsync()).Should().Be(first, "only the token that was rejected is dropped");

        service.Invalidate(first);
        var replacement = await service.GetServiceTokenAsync();

        replacement.Should().NotBe(first);
        identity.Requests.Should().Be(2);
    }

    [Fact]
    public async Task ACallerWhoGivesUp_IsNotAnIdentityFailure()
    {
        var (service, identity, _) = Create();
        identity.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.Invoking(s => s.GetServiceTokenAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();

        identity.Respond = (_, _) => Task.FromResult(identity.Token(900));
        (await service.GetServiceTokenAsync()).Should().StartWith("token-", "no waiting period was started");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}""")]
    [InlineData(HttpStatusCode.OK, """{"unexpected":true}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task AnAnswerWithoutAToken_IsAFailure(HttpStatusCode status, string body)
    {
        var (service, identity, _) = Create();
        identity.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

        await service.Invoking(s => s.GetServiceTokenAsync()).Should().ThrowAsync<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task AMissingClientSecret_IsReportedAsNoToken()
    {
        var (service, _, _) = Create(new Dictionary<string, string?> { ["IdentityServer:Authority"] = "http://identity.test" });

        var failure = await service.Invoking(s => s.GetServiceTokenAsync()).Should().ThrowAsync<ServiceTokenUnavailableException>();

        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("ServiceAuth:ClientSecret");
    }
}
