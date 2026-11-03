using FluentAssertions;
using ShopEasy.EndToEndTests.Support;
using ShopEasy.Probe;
using Xunit;

namespace ShopEasy.UnitTests.Probe;

public class ProbeSettingsTests
{
    private static ProbeSettings Valid() => new() { Email = "probe@example.test", Password = "Probe-Pa55word-1" };

    [Fact]
    public void WithAnAccount_TheSettingsAreFine()
        => Valid().Problem().Should().BeNull();

    [Theory]
    [InlineData("", "Probe-Pa55word-1", "Probe__Email")]
    [InlineData("  ", "Probe-Pa55word-1", "Probe__Email")]
    [InlineData("probe@example.test", "", "Probe__Password")]
    public void WithoutAnAccount_TheProbeSaysWhichSettingIsMissing_AndWhereItIsSet(string email, string password, string setting)
        => new ProbeSettings { Email = email, Password = password }.Problem()
            .Should().Contain(setting).And.Contain(".env");

    [Fact]
    public void APollingIntervalThatCouldUseUpTheGatewaysLimit_IsRefused()
    {
        var settings = Valid();
        settings.PollMilliseconds = ProbeSettings.MinPollMilliseconds - 1;

        settings.Problem().Should().Contain("request limit");
    }

    [Fact]
    public void TheDefaults_AreOneRunAMinute_OnTheFirstProduct_OnTheMetricsPortOfTheServices()
    {
        var defaults = new ProbeSettings();

        defaults.Interval.Should().Be(TimeSpan.FromSeconds(60));
        defaults.StepTimeout.Should().Be(TimeSpan.FromSeconds(60));
        defaults.ProductId.Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        defaults.MetricsPort.Should().Be(9091);
        defaults.PollMilliseconds.Should().BeGreaterThanOrEqualTo(ProbeSettings.MinPollMilliseconds);
    }

    [Fact]
    public void ARunNeverStartsMoreOftenThanEveryFiveSeconds()
        => new ProbeSettings { IntervalSeconds = 0 }.Interval.Should().Be(TimeSpan.FromSeconds(5));
}

// The probe signs in with the addresses a browser uses (the gateway's redirects name them), inside a network
// where those addresses do not exist: only the connection is sent elsewhere.
public class AddressMapTests
{
    [Fact]
    public void TheSetting_IsAListOfPublicAddressesAndTheContainerBehindEach()
    {
        var map = Stack.ParseAddressMap(" localhost:5000=gateway:8080, LocalHost:5001 = identity:8080 ");

        map.Should().HaveCount(2);
        map["localhost:5000"].Should().Be("gateway:8080");
        map["localhost:5001"].Should().Be("identity:8080", "the public address is matched without regard to case");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("=gateway:8080")]
    [InlineData("localhost:5000=")]
    public void NothingUsable_IsAnEmptyMap(string? setting)
        => Stack.ParseAddressMap(setting).Should().BeEmpty();

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? HostHeader { get; private set; }
        public Uri? Target { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            HostHeader = request.Headers.Host;
            Target = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static async Task<Recorder> SendAsync(string url, string map = "localhost:5000=gateway:8080,localhost:5001=identity:8080", string? host = null)
    {
        var recorder = new Recorder();
        using var client = new HttpMessageInvoker(new AddressMapHandler(Stack.ParseAddressMap(map), recorder));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (host is not null)
            request.Headers.Host = host;
        await client.SendAsync(request, CancellationToken.None);
        return recorder;
    }

    [Theory]
    [InlineData("http://localhost:5000/account/login?x=1", "http://gateway:8080/account/login?x=1", "localhost:5000")]
    [InlineData("http://localhost:5001/Account/Login", "http://identity:8080/Account/Login", "localhost:5001")]
    public async Task ARequestForAPublicAddress_IsConnectedToTheContainer_ButStillAddressedToThePublicOne(string url, string connectsTo, string hostHeader)
    {
        var sent = await SendAsync(url);

        sent.Target.Should().Be(new Uri(connectsTo));
        sent.HostHeader.Should().Be(hostHeader,
            "the gateway builds the sign-in's redirect address from the Host header, and Identity only accepts the public one");
    }

    [Fact]
    public async Task AnAddressThatIsNotInTheMap_IsLeftAlone()
    {
        var sent = await SendAsync("http://example.test:5000/anything");

        sent.Target.Should().Be(new Uri("http://example.test:5000/anything"));
        sent.HostHeader.Should().BeNull();
    }

    [Fact]
    public async Task AHostHeaderThatWasSetOnPurpose_IsNotOverwritten()
    {
        var sent = await SendAsync("http://localhost:5000/x", host: "shop.example.test");

        sent.HostHeader.Should().Be("shop.example.test");
    }
}
