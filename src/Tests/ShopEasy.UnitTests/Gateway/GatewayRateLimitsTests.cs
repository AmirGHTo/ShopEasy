using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ShopEasy.Gateway.Security;
using Xunit;

namespace ShopEasy.UnitTests.Gateway;

// How many requests the gateway lets one caller make in a minute, per kind of caller.
public class GatewayRateLimitsTests
{
    private static HttpContext Anonymous(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    private static HttpContext SignedIn(string userId, string address = "203.0.113.7", bool admin = false)
    {
        var context = Anonymous(address);
        var claims = new List<Claim> { new("sub", userId) };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "admin"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }

    private static int Allowed(PartitionedRateLimiter<HttpContext> limiter, Func<HttpContext> request, int tries)
    {
        var allowed = 0;
        for (var i = 0; i < tries; i++)
        {
            using var lease = limiter.AttemptAcquire(request());
            if (lease.IsAcquired) allowed++;
        }
        return allowed;
    }

    private static PartitionedRateLimiter<HttpContext> Limiter(
        Func<HttpContext, GatewayRateLimitSettings, RateLimitPartition<string>> policy, GatewayRateLimitSettings settings) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => policy(context, settings));

    // ---- the defaults

    [Fact]
    public void TheDefaults_LetACustomerBrowseAndTheShopStayProtected()
    {
        var settings = new GatewayRateLimitSettings();

        // a page view is about three requests: 120 a minute is some forty page views
        settings.AnonymousPerMinute.Should().Be(120);
        settings.AuthenticatedPerMinute.Should().Be(100);
        settings.AdminPerMinute.Should().Be(500);
        settings.OrdersPerMinute.Should().Be(10);
    }

    // ---- anonymous: per client address

    [Fact]
    public void AnAnonymousClient_MayMakeThePermittedNumberOfRequests_AndOtherAddressesAreNotAffected()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAnonymous, new GatewayRateLimitSettings { AnonymousPerMinute = 5 });

        Allowed(limiter, () => Anonymous("203.0.113.1"), 9).Should().Be(5);
        Allowed(limiter, () => Anonymous("203.0.113.2"), 9).Should().Be(5);
    }

    [Fact]
    public void ADefaultAnonymousClient_CanLoadMoreThanThirtyRequestsAMinute()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAnonymous, new GatewayRateLimitSettings());

        Allowed(limiter, () => Anonymous("203.0.113.1"), 200).Should().Be(120);
    }

    [Fact]
    public void WithoutAnAddress_AllRequestsShareOneBudget()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAnonymous, new GatewayRateLimitSettings { AnonymousPerMinute = 3 });

        Allowed(limiter, () => new DefaultHttpContext(), 5).Should().Be(3);
    }

    // ---- authenticated: per user, administrators higher

    [Fact]
    public void ASignedInUser_HasTheirOwnBudget_WhateverTheAddress()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAuthenticated, new GatewayRateLimitSettings { AuthenticatedPerMinute = 4 });

        Allowed(limiter, () => SignedIn("u-1", "203.0.113.1"), 6).Should().Be(4);
        Allowed(limiter, () => SignedIn("u-1", "198.51.100.9"), 6).Should().Be(0); // the same user from another address
        Allowed(limiter, () => SignedIn("u-2", "203.0.113.1"), 6).Should().Be(4); // another user on the same address
    }

    [Fact]
    public void AnAdministrator_HasTheHigherLimit()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAuthenticated,
            new GatewayRateLimitSettings { AuthenticatedPerMinute = 4, AdminPerMinute = 9 });

        Allowed(limiter, () => SignedIn("admin-1", admin: true), 20).Should().Be(9);
        Allowed(limiter, () => SignedIn("u-1"), 20).Should().Be(4);
    }

    [Fact]
    public void SomeoneNotSignedIn_ReachingAProtectedRoute_IsCountedByAddress()
    {
        using var limiter = Limiter(GatewayRateLimits.ForAuthenticated, new GatewayRateLimitSettings { AuthenticatedPerMinute = 2 });

        Allowed(limiter, () => Anonymous("203.0.113.1"), 5).Should().Be(2);
    }

    // ---- orders: per user, apart from everything else they do

    [Fact]
    public void PlacingOrders_HasItsOwnSmallBudgetPerUser()
    {
        var settings = new GatewayRateLimitSettings { AuthenticatedPerMinute = 50, OrdersPerMinute = 3 };
        using var orders = Limiter(GatewayRateLimits.ForOrders, settings);
        using var others = Limiter(GatewayRateLimits.ForAuthenticated, settings);

        Allowed(orders, () => SignedIn("u-1"), 8).Should().Be(3);
        Allowed(orders, () => SignedIn("u-2"), 8).Should().Be(3);
        Allowed(others, () => SignedIn("u-1"), 8).Should().Be(8, "placing orders does not use up the rest of the budget");
    }

    // ---- validation

    [Fact]
    public void ValidSettings_AreAccepted()
    {
        new GatewayRateLimitSettings().Invoking(s => s.Validate()).Should().NotThrow();
        new GatewayRateLimitSettings { AnonymousPerMinute = 1, OrdersPerMinute = GatewayRateLimitSettings.Highest }
            .Invoking(s => s.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(nameof(GatewayRateLimitSettings.AnonymousPerMinute), 0)]
    [InlineData(nameof(GatewayRateLimitSettings.AuthenticatedPerMinute), -5)]
    [InlineData(nameof(GatewayRateLimitSettings.AdminPerMinute), 0)]
    [InlineData(nameof(GatewayRateLimitSettings.OrdersPerMinute), GatewayRateLimitSettings.Highest + 1)]
    public void ANumberOutsideTheRange_StopsTheGateway_NamingTheSetting(string setting, int value)
    {
        var settings = new GatewayRateLimitSettings();
        typeof(GatewayRateLimitSettings).GetProperty(setting)!.SetValue(settings, value);

        settings.Invoking(s => s.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"RateLimiting:{setting} must be between 1 and 100000, not {value}.");
    }

    // ---- reading the configuration

    [Fact]
    public void TheSettings_AreReadFromTheRateLimitingSection_AndTheRestKeepsItsDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:AnonymousPerMinute"] = "600",
            ["RateLimiting:OrdersPerMinute"] = "20",
            // the Identity service's keys live in the same section of a shared configuration
            ["RateLimiting:Login:PermitLimit"] = "10",
        }).Build();

        var settings = GatewayRateLimitSettings.From(configuration);

        settings.AnonymousPerMinute.Should().Be(600);
        settings.OrdersPerMinute.Should().Be(20);
        settings.AuthenticatedPerMinute.Should().Be(100);
        settings.AdminPerMinute.Should().Be(500);
    }

    [Fact]
    public void WithoutASection_TheDefaultsApply()
    {
        var settings = GatewayRateLimitSettings.From(new ConfigurationBuilder().Build());

        settings.AnonymousPerMinute.Should().Be(new GatewayRateLimitSettings().AnonymousPerMinute);
    }
}
