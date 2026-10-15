using Duende.IdentityServer.Models;
using ShopEasy.Shared.Security;
using Scopes = ShopEasy.Shared.Security.ApiScopes;

namespace ShopEasy.Identity.Configuration;

public static class IdentityServerConfig
{
    // Scopes define what data about the user can be requested
    public static IEnumerable<IdentityResource> IdentityResources =>
        new IdentityResource[]
        {
            new IdentityResources.OpenId(),   // sub claim (user ID)
            new IdentityResources.Profile(),  // name, given_name, family_name
            new IdentityResources.Email(),    // email, email_verified
        };

    private static readonly string[] IdentityScopes = { "openid", "profile", "email" };

    // API scopes are the permissions a client can be granted for an API. Services enforce them on
    // their endpoints (AccessPolicies in ShopEasy.Shared): a token without the scope gets 403.
    public static IEnumerable<ApiScope> ApiScopes =>
        new[]
        {
            new ApiScope(Scopes.ProductsRead,   "Read the product catalog"),
            new ApiScope(Scopes.ProductsWrite,  "Create and manage products"),
            new ApiScope(Scopes.OrdersRead,     "Read orders"),
            new ApiScope(Scopes.OrdersWrite,    "Place and cancel orders"),
            new ApiScope(Scopes.InventoryRead,  "Read stock levels"),
            new ApiScope(Scopes.InventoryWrite, "Adjust stock levels"),
            new ApiScope(Scopes.PaymentRead,    "Read payments"),
        };

    // One API resource per API. The resource name becomes the audience ("aud") of every token that
    // carries one of its scopes, and each service accepts only tokens whose audience is its own:
    // a token obtained for Inventory is useless against Payment, and the token the Order service
    // gets for itself names Inventory only.
    public static IEnumerable<ApiResource> ApiResources =>
        new[]
        {
            new ApiResource(ApiAudiences.Product, "ShopEasy Product API")
            {
                Scopes = { Scopes.ProductsRead, Scopes.ProductsWrite }
            },
            new ApiResource(ApiAudiences.Order, "ShopEasy Order API")
            {
                Scopes = { Scopes.OrdersRead, Scopes.OrdersWrite }
            },
            new ApiResource(ApiAudiences.Inventory, "ShopEasy Inventory API")
            {
                Scopes = { Scopes.InventoryRead, Scopes.InventoryWrite }
            },
            new ApiResource(ApiAudiences.Payment, "ShopEasy Payment API")
            {
                Scopes = { Scopes.PaymentRead }
            },
        };

    // Clients are registered applications that can request tokens
    public static IEnumerable<Client> GetClients(IConfiguration configuration)
    {
        // Secrets are never hardcoded — they come from configuration (env vars / secret store)
        var gatewaySecret = configuration["Clients:Gateway:Secret"]
            ?? throw new InvalidOperationException("Clients:Gateway:Secret is not configured.");
        var orderServiceSecret = configuration["Clients:OrderService:Secret"]
            ?? throw new InvalidOperationException("Clients:OrderService:Secret is not configured.");
        var gatewayUrl = (configuration["Clients:Gateway:PublicUrl"] ?? "http://localhost:5000").TrimEnd('/');

        return new[]
        {
            // ─────────────────────────────────────────────────────────
            // Client 1: The API Gateway (acting as BFF for the React SPA)
            // Uses Authorization Code flow with a client secret
            // ─────────────────────────────────────────────────────────
            new Client
            {
                ClientId = "shopeasy-gateway",
                ClientName = "ShopEasy Web Gateway (BFF)",
                ClientSecrets = { new Secret(gatewaySecret.Sha256()) },

                AllowedGrantTypes = GrantTypes.Code,

                // Where to redirect after login

                   RedirectUris =
    {
        $"{gatewayUrl}/signin-oidc",    // browser-facing gateway URL
        "http://gateway:8080/signin-oidc"       // internal Docker (future use)
    },

    PostLogoutRedirectUris =
    {
        $"{gatewayUrl}/signout-callback-oidc",
        "http://gateway:8080/signout-callback-oidc"
    },



                // Cookie-based session for the BFF
                AllowOfflineAccess = true, // enables Refresh Tokens

                // The scopes the gateway may ask for on behalf of a signed-in user. They are not
                // enough on their own: the services also check the user's role for administration.
                AllowedScopes = IdentityScopes.Concat(Scopes.Web).ToList(),

                // Token lifetimes
                AccessTokenLifetime  = 3600,        // 1 hour
                IdentityTokenLifetime = 300,         // 5 minutes
                AbsoluteRefreshTokenLifetime = 2592000, // 30 days
                SlidingRefreshTokenLifetime  = 1296000, // 15 days
                RefreshTokenUsage = TokenUsage.ReUse,
                RefreshTokenExpiration = TokenExpiration.Sliding,
            },

            // ─────────────────────────────────────────────────────────
            // Client 2: Mobile App (public client — no secret, uses PKCE)
            // ─────────────────────────────────────────────────────────
            new Client
            {
                ClientId = "shopeasy-mobile",
                ClientName = "ShopEasy Mobile App",
                ClientSecrets = { }, // No secret for public clients

                AllowedGrantTypes = GrantTypes.Code,
                RequireClientSecret = false, // Public client
                RequirePkce = true,          // PKCE replaces the secret

                RedirectUris = { "shopeasy://callback" },
                PostLogoutRedirectUris = { "shopeasy://logout" },

                AllowOfflineAccess = true,

                // Customer shopping only: no catalog or stock administration scopes
                AllowedScopes = IdentityScopes.Concat(Scopes.Mobile).ToList(),

                AccessTokenLifetime = 1800, // 30 min (shorter for mobile)
                AbsoluteRefreshTokenLifetime = 7776000, // 90 days
                RefreshTokenUsage = TokenUsage.OneTimeOnly, // Rotate on each use
                RefreshTokenExpiration = TokenExpiration.Sliding,
            },

            // ─────────────────────────────────────────────────────────
            // Client 3: Order Service (machine-to-machine)
            // Reads stock levels from Inventory as itself (the stock check
            // over gRPC); it holds no other permission
            // ─────────────────────────────────────────────────────────
            new Client
            {
                ClientId = "order-service",
                ClientName = "ShopEasy Order Service (M2M)",
                ClientSecrets = { new Secret(orderServiceSecret.Sha256()) },

                AllowedGrantTypes = GrantTypes.ClientCredentials,

                AllowedScopes = Scopes.OrderService.ToList(),

                AccessTokenLifetime = 900, // 15 min for service tokens
            },
        };
    }
}
