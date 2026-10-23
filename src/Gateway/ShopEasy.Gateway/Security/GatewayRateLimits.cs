using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ShopEasy.Gateway.Security;

// Settings under "RateLimiting": how many requests a minute the gateway lets through for each kind of
// caller. The limits protect the services behind it from one client that asks too much; they are not
// meant to be felt by someone shopping.
//
// A page of the storefront asks for the session, the products and the categories, so one visit is
// about three requests and 120 a minute allows a customer some forty page views. The storefront's own
// files and its page shell are not limited at all (they are static files and cost the services
// nothing); the limits are on the APIs.
public sealed class GatewayRateLimitSettings
{
    public const string SectionName = "RateLimiting";
    public const int Highest = 100_000;

    // browsing the shop without signing in (products, categories, the session check): per client address
    public int AnonymousPerMinute { get; set; } = 120;

    // everything behind sign-in, including reading and cancelling orders: per user
    public int AuthenticatedPerMinute { get; set; } = 100;

    // the same for an administrator
    public int AdminPerMinute { get; set; } = 500;

    // placing orders only (the Order service has a limit of its own as a second line): per user
    public int OrdersPerMinute { get; set; } = 10;

    public static GatewayRateLimitSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<GatewayRateLimitSettings>() ?? new GatewayRateLimitSettings();

    public void Validate()
    {
        Check(nameof(AnonymousPerMinute), AnonymousPerMinute);
        Check(nameof(AuthenticatedPerMinute), AuthenticatedPerMinute);
        Check(nameof(AdminPerMinute), AdminPerMinute);
        Check(nameof(OrdersPerMinute), OrdersPerMinute);
    }

    private static void Check(string name, int value)
    {
        if (value < 1 || value > Highest)
            throw new InvalidOperationException($"{SectionName}:{name} must be between 1 and {Highest}, not {value}.");
    }
}

// The named policies the routes and endpoints ask for ("anonymous", "authenticated", "orders").
public static class GatewayRateLimits
{
    public const string Anonymous = "anonymous";
    public const string Authenticated = "authenticated";
    public const string Orders = "orders";

    public static void AddGatewayPolicies(this RateLimiterOptions options, GatewayRateLimitSettings settings)
    {
        options.AddPolicy(Anonymous, context => ForAnonymous(context, settings));
        options.AddPolicy(Authenticated, context => ForAuthenticated(context, settings));
        options.AddPolicy(Orders, context => ForOrders(context, settings));
    }

    // Per client address
    public static RateLimitPartition<string> ForAnonymous(HttpContext context, GatewayRateLimitSettings settings) =>
        Window(Address(context), settings.AnonymousPerMinute);

    // Per user (the "sub" claim); admins have a higher limit. Without a user: per client address.
    public static RateLimitPartition<string> ForAuthenticated(HttpContext context, GatewayRateLimitSettings settings)
    {
        var key = context.User.FindFirst("sub")?.Value ?? Address(context);
        return Window(key, context.User.IsInRole("admin") ? settings.AdminPerMinute : settings.AuthenticatedPerMinute);
    }

    // Per user, apart from the budget of everything else they do
    public static RateLimitPartition<string> ForOrders(HttpContext context, GatewayRateLimitSettings settings) =>
        Window($"orders:{context.User.FindFirst("sub")?.Value ?? Address(context)}", settings.OrdersPerMinute);

    private static string Address(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Window(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
}
