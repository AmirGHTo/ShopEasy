using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ShopEasy.Shared.Security;

namespace ShopEasy.UnitTests.Support;

// A stand-in for Identity: signs access tokens the way Identity does (the claims it issues:
// "aud" and "scope" as lists, "sub", "role", "client_id") so the APIs can be tested with real
// bearer-token validation and without a running identity server.
internal sealed class TestIdentityProvider
{
    public const string Issuer = "http://identity.test";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    // The discovery document the APIs would fetch from Identity: its issuer and signing key
    public static StaticConfigurationManager<OpenIdConnectConfiguration> ConfigurationManager()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(Key);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }

    public sealed record Token(
        IEnumerable<string> Audiences,
        IEnumerable<string> Scopes,
        string? Subject = null,
        string? ClientId = null,
        IEnumerable<string>? Roles = null,
        TimeSpan? Age = null,
        string Issuer = Issuer,
        bool SignedByAnotherKey = false)
    {
        public string Issue()
        {
            var now = DateTime.UtcNow;
            // an Age shifts the whole lifetime into the past (a token is valid for an hour)
            var issuedAt = now - (Age ?? TimeSpan.Zero);

            var claims = new Dictionary<string, object>
            {
                ["aud"] = Audiences.ToArray(),
                ["scope"] = Scopes.ToArray(),
            };
            if (Subject is not null) claims[ClaimNames.Subject] = Subject;
            if (ClientId is not null) claims[ClaimNames.ClientId] = ClientId;
            var roles = Roles?.ToArray() ?? [];
            if (roles.Length == 1) claims[ClaimNames.Role] = roles[0];
            else if (roles.Length > 1) claims[ClaimNames.Role] = roles;

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Claims = claims,
                NotBefore = issuedAt,
                IssuedAt = issuedAt,
                Expires = issuedAt.AddHours(1),
                SigningCredentials = new SigningCredentials(SignedByAnotherKey ? OtherKey : Key, SecurityAlgorithms.RsaSha256),
            });
        }
    }

    // A token that claims no signature at all ("alg": "none")
    public static string Unsigned(IEnumerable<string> audiences, IEnumerable<string> scopes)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = $$"""{"iss":"{{Issuer}}","aud":[{{string.Join(',', audiences.Select(a => $"\"{a}\""))}}],"scope":[{{string.Join(',', scopes.Select(a => $"\"{a}\""))}}],"sub":"u1","role":"admin","exp":{{exp}}}""";
        return $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64(payload)}.";
    }

    // The principals of the access tests. All tokens are valid and signed by the test Identity
    // except where the name says otherwise.
    public static class Principals
    {
        public static readonly string[] AllAudiences = [.. ApiAudiences.All];

        // a signed-in customer using the web gateway: every scope the gateway can ask for
        public static Token Customer() => new(AllAudiences, ["openid", "profile", .. ApiScopes.Web], Subject: "customer-1", ClientId: "shopeasy-gateway", Roles: ["customer"]);

        public static Token Admin() => new(AllAudiences, ["openid", "profile", .. ApiScopes.Web], Subject: "admin-1", ClientId: "shopeasy-gateway", Roles: [Roles.Admin]);

        // a client that was not given any API scope
        public static Token CustomerWithoutScopes() => new(AllAudiences, ["openid", "profile"], Subject: "customer-1", ClientId: "some-client", Roles: ["customer"]);

        // an administrator, but through a client that was not given any API scope
        public static Token AdminWithoutScopes() => new(AllAudiences, ["openid", "profile"], Subject: "admin-1", ClientId: "some-client", Roles: [Roles.Admin]);

        // everything right, but meant for another API
        public static Token ForAnotherApi(string api) =>
            new(ApiAudiences.All.Where(a => a != api).Take(1), ["openid", "profile", .. ApiScopes.Web], Subject: "admin-1", ClientId: "shopeasy-gateway", Roles: [Roles.Admin]);

        // what the Order service gets for itself (client credentials): Inventory only, no user
        public static Token OrderService() => new([ApiAudiences.Inventory], [.. ApiScopes.OrderService], ClientId: "order-service");
    }
}
