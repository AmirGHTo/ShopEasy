using Duende.IdentityServer.Models;

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

    // API scopes define what services/actions clients can access
    public static IEnumerable<ApiScope> ApiScopes =>
        new[]
        {
            // Coarse-grained scopes (what the gateway requests for users)
            new ApiScope("products.read",     "Read product catalog"),
            new ApiScope("products.write",    "Create and manage products"),
            new ApiScope("orders.read",       "Read own orders"),
            new ApiScope("orders.write",      "Place and manage orders"),

            // Fine-grained scopes (for service-to-service communication)
            new ApiScope("inventory.read",    "Read stock levels (internal)"),
            new ApiScope("inventory.write",   "Adjust stock levels (internal)"),
            new ApiScope("payment.charge",    "Process payments (internal)"),
        };

    // API resources group scopes and define the audience claim in the JWT
    public static IEnumerable<ApiResource> ApiResources =>
        new[]
        {
            new ApiResource("shopeasy-api", "ShopEasy Platform API")
            {
                // All services validate tokens with audience = "shopeasy-api"
                Scopes =
                {
                    "products.read", "products.write",
                    "orders.read",   "orders.write",
                    "inventory.read", "inventory.write",
                    "payment.charge"
                }
            }
        };

    // Clients are registered applications that can request tokens
    public static IEnumerable<Client> Clients =>
        new[]
        {
            // ─────────────────────────────────────────────────────────
            // Client 1: The API Gateway (acting as BFF for the React SPA)
            // Uses Authorization Code flow with a client secret
            // ─────────────────────────────────────────────────────────
            new Client
            {
                ClientId = "shopeasy-gateway",
                ClientName = "ShopEasy Web Gateway (BFF)",
                ClientSecrets = { new Secret("gateway-secret".Sha256()) },

                AllowedGrantTypes = GrantTypes.Code,

                // Where to redirect after login

                   RedirectUris =
    {
        "http://localhost:5000/signin-oidc",    // browser on host machine
        "http://gateway:8080/signin-oidc"       // internal Docker (future use)
    },

    PostLogoutRedirectUris =
    {
        "http://localhost:5000/signout-callback-oidc",
        "http://gateway:8080/signout-callback-oidc"
    },



                // Cookie-based session for the BFF
                AllowOfflineAccess = true, // enables Refresh Tokens

                AllowedScopes =
                {
                    "openid", "profile", "email",
                    "products.read", "products.write",
                    "orders.read", "orders.write"
                },

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

                AllowedScopes =
                {
                    "openid", "profile", "email",
                    "products.read", "products.write",
                    "orders.read", "orders.write"
                },

                AccessTokenLifetime = 1800, // 30 min (shorter for mobile)
                AbsoluteRefreshTokenLifetime = 7776000, // 90 days
                RefreshTokenUsage = TokenUsage.OneTimeOnly, // Rotate on each use
                RefreshTokenExpiration = TokenExpiration.Sliding,
            },

            // ─────────────────────────────────────────────────────────
            // Client 3: Order Service (machine-to-machine)
            // Calls Inventory and Payment services as itself
            // ─────────────────────────────────────────────────────────
            new Client
            {
                ClientId = "order-service",
                ClientName = "ShopEasy Order Service (M2M)",
                ClientSecrets = { new Secret("order-service-secret".Sha256()) },

                AllowedGrantTypes = GrantTypes.ClientCredentials,

                AllowedScopes =
                {
                    "inventory.read",
                    "inventory.write",
                    "payment.charge"
                },

                AccessTokenLifetime = 900, // 15 min for service tokens
            },
        };
}
