using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// A call to Inventory as the Order service itself: the service token goes on the call, a missing
// token is not an Inventory failure, and a token Inventory refuses is dropped.
public class InventoryServiceCallTests
{
    private static InventoryCallPolicy Policy() =>
        new(Options.Create(new InventoryCallOptions { TimeoutSeconds = 1, MaxRetries = 2, BreakDurationSeconds = 1 }),
            Options.Create(new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 100 }),
            NullLogger<InventoryCallPolicy>.Instance);

    private static (InventoryServiceCall Call, ITokenService Tokens) Create(string token = "service-token")
    {
        var tokens = Substitute.For<ITokenService>();
        tokens.GetServiceTokenAsync(Arg.Any<CancellationToken>()).Returns(token);
        return (new InventoryServiceCall(Policy(), tokens, NullLogger<InventoryServiceCall>.Instance), tokens);
    }

    private static RpcException Failure(StatusCode code) => new(new Status(code, code.ToString()));

    [Fact]
    public async Task TheServiceToken_IsSentAsABearerToken()
    {
        var (call, _) = Create("abc");
        Metadata? sent = null;

        var result = await call.ExecuteAsync((headers, _, _) => { sent = headers; return Task.FromResult(42); });

        result.Should().Be(42);
        sent!.GetValue("authorization").Should().Be("Bearer abc");
    }

    [Fact]
    public async Task TheTokenIsFetchedOnce_EvenWhenTheCallIsRetried()
    {
        var (call, tokens) = Create();
        var attempts = 0;

        await call.ExecuteAsync((_, _, _) =>
            ++attempts < 3 ? Task.FromException<int>(Failure(StatusCode.Unavailable)) : Task.FromResult(1));

        attempts.Should().Be(3);
        await tokens.Received(1).GetServiceTokenAsync(Arg.Any<CancellationToken>());
        tokens.DidNotReceive().Invalidate(Arg.Any<string>());
    }

    [Fact]
    public async Task WithoutAToken_NothingIsSent_AndInventoryIsReportedUnavailable()
    {
        var (call, tokens) = Create();
        tokens.GetServiceTokenAsync(Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new ServiceTokenUnavailableException("Identity is down"));
        var called = false;

        var failure = await call.Invoking(c => c.ExecuteAsync((_, _, _) => { called = true; return Task.FromResult(1); }))
            .Should().ThrowAsync<InventoryUnavailableException>();

        called.Should().BeFalse();
        failure.Which.InnerException.Should().BeOfType<ServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task IdentityBeingDown_DoesNotOpenTheInventoryCircuit()
    {
        var (call, tokens) = Create();
        var identityDown = true;
        tokens.GetServiceTokenAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            identityDown ? throw new ServiceTokenUnavailableException("down") : Task.FromResult("service-token"));

        for (var i = 0; i < 20; i++)
            await call.Invoking(c => c.ExecuteAsync((_, _, _) => Task.FromResult(1))).Should().ThrowAsync<InventoryUnavailableException>();
        identityDown = false;

        // the breaker never saw a failed Inventory call, so the first call with a token goes through
        (await call.ExecuteAsync((_, _, _) => Task.FromResult(7))).Should().Be(7);
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task ATokenInventoryRefuses_IsDropped_SoTheNextCallGetsANewOne(StatusCode refusal)
    {
        var (call, tokens) = Create("old-token");

        await call.Invoking(c => c.ExecuteAsync((_, _, _) => Task.FromException<int>(Failure(refusal))))
            .Should().ThrowAsync<InventoryUnavailableException>();

        tokens.Received(1).Invalidate("old-token");
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task OtherFailures_LeaveTheTokenAlone(StatusCode code)
    {
        var (call, tokens) = Create();

        await call.Invoking(c => c.ExecuteAsync((_, _, _) => Task.FromException<int>(Failure(code))))
            .Should().ThrowAsync<InventoryUnavailableException>();

        tokens.DidNotReceive().Invalidate(Arg.Any<string>());
    }

    [Fact]
    public async Task ACallerWhoGivesUp_GetsACancellation()
    {
        var (call, _) = Create();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await call.Invoking(c => c.ExecuteAsync((_, _, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 1), cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
