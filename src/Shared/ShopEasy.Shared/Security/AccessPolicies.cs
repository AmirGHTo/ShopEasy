using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ShopEasy.Shared.Security;

// Names of the authorization policies, for [Authorize(Policy = ...)]. A policy name says what it
// needs: "orders.read" is the scope alone, "orders.read:admin" the scope and the admin role,
// "inventory.read:service" the scope on a token a service got for itself.
public static class PolicyNames
{
    public const string ProductsWrite = "products.write:admin";

    public const string OrdersRead = "orders.read";
    public const string OrdersWrite = "orders.write";
    public const string OrdersMonitor = "orders.read:admin";
    public const string OrdersMonitorWrite = "orders.write:admin";

    public const string InventoryRead = "inventory.read";
    public const string InventoryAudit = "inventory.read:admin";
    public const string InventoryAdjust = "inventory.write:admin";
    public const string InventoryInternal = "inventory.read:service";

    public const string PaymentRead = "payment.read";
    public const string PaymentMonitor = "payment.read:admin";
    public const string PaymentInternal = "payment.read:service";
}

// One authorization policy: the scope the token must carry, plus optionally the admin role (an
// administrator's token from a client that was not given the scope still does not get in) or a
// token issued to a service rather than to a user.
public sealed record AccessPolicy(string Name, string Scope, bool RequireAdmin = false, bool RequireServiceToken = false);

// The policies of each API. The same lists are registered by the services and checked by the
// tests, so what is enforced is what is written here.
public static class AccessPolicies
{
    public static IReadOnlyList<AccessPolicy> Product { get; } =
    [
        new(PolicyNames.ProductsWrite, ApiScopes.ProductsWrite, RequireAdmin: true),
    ];

    public static IReadOnlyList<AccessPolicy> Order { get; } =
    [
        new(PolicyNames.OrdersRead, ApiScopes.OrdersRead),
        new(PolicyNames.OrdersWrite, ApiScopes.OrdersWrite),
        new(PolicyNames.OrdersMonitor, ApiScopes.OrdersRead, RequireAdmin: true),
        new(PolicyNames.OrdersMonitorWrite, ApiScopes.OrdersWrite, RequireAdmin: true),
    ];

    public static IReadOnlyList<AccessPolicy> Inventory { get; } =
    [
        new(PolicyNames.InventoryRead, ApiScopes.InventoryRead),
        new(PolicyNames.InventoryAudit, ApiScopes.InventoryRead, RequireAdmin: true),
        new(PolicyNames.InventoryAdjust, ApiScopes.InventoryWrite, RequireAdmin: true),
        new(PolicyNames.InventoryInternal, ApiScopes.InventoryRead, RequireServiceToken: true),
    ];

    public static IReadOnlyList<AccessPolicy> Payment { get; } =
    [
        new(PolicyNames.PaymentRead, ApiScopes.PaymentRead),
        new(PolicyNames.PaymentMonitor, ApiScopes.PaymentRead, RequireAdmin: true),
        new(PolicyNames.PaymentInternal, ApiScopes.PaymentRead, RequireServiceToken: true),
    ];
}

public static class AccessPolicyExtensions
{
    // Registers the policies and makes every endpoint that does not say otherwise require a
    // signed-in caller. A new endpoint is therefore closed until someone opens it on purpose
    // ([AllowAnonymous] or a policy), instead of open until someone remembers to close it.
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services, IEnumerable<AccessPolicy> policies)
    {
        var list = policies.ToList();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            foreach (var policy in list)
                options.AddPolicy(policy.Name, builder => Build(builder, policy));
        });
        return services;
    }

    public static AuthorizationPolicy ToAuthorizationPolicy(this AccessPolicy policy)
    {
        var builder = new AuthorizationPolicyBuilder();
        Build(builder, policy);
        return builder.Build();
    }

    private static void Build(AuthorizationPolicyBuilder builder, AccessPolicy policy)
    {
        builder.RequireAuthenticatedUser();
        builder.RequireAssertion(context => context.User.HasScope(policy.Scope));
        if (policy.RequireAdmin)
            builder.RequireRole(Roles.Admin);
        if (policy.RequireServiceToken)
            builder.RequireAssertion(context => context.User.IsServiceToken());
    }
}

public static class ClaimsPrincipalExtensions
{
    // The scopes of the token. Identity writes them as one claim per scope; a space-delimited list
    // in a single claim (the other form the specification allows) is understood as well.
    public static IEnumerable<string> Scopes(this ClaimsPrincipal user) =>
        user.FindAll(ClaimNames.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.Scopes().Contains(scope, StringComparer.Ordinal);

    // A token a service obtained for itself (client credentials) names the client and has no user:
    // a token issued on behalf of a signed-in user always has a subject.
    public static bool IsServiceToken(this ClaimsPrincipal user) =>
        user.HasClaim(claim => claim.Type == ClaimNames.ClientId)
        && !user.HasClaim(claim => claim.Type == ClaimNames.Subject);
}
