using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace ShopEasy.Shared.Security;

// Settings under "IdentityServer", read by every API and by the gateway.
public class ApiAuthenticationOptions
{
    public const string SectionName = "IdentityServer";

    // Where the services reach Identity (its discovery document and signing keys are fetched from
    // here). Inside a private network this is the internal address, e.g. http://identity:8080.
    public string Authority { get; set; } = "http://identity:8080";

    // Refuse to fetch the discovery document and the signing keys over plain HTTP. Switch it on as
    // soon as Authority is an https:// address (a managed identity provider, or Identity behind a
    // TLS endpoint inside the cluster). It stays off for the default http://identity:8080, where
    // the traffic never leaves the cluster network; whoever can alter that traffic could hand the
    // services their own signing key, so that network has to be trusted (network policies, mesh).
    public bool RequireHttpsMetadata { get; set; }

    public static ApiAuthenticationOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<ApiAuthenticationOptions>() ?? new ApiAuthenticationOptions();

    public void Validate()
    {
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Authority)} must be an http:// or https:// address, not '{Authority}'.");
        if (RequireHttpsMetadata && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(RequireHttpsMetadata)} is on but {SectionName}:{nameof(Authority)} is '{Authority}'. " +
                "Use an https:// address or switch the setting off.");
    }
}

public static class ApiAuthenticationExtensions
{
    // Bearer-token validation for an API. The token must be signed by Identity, unexpired, issued by
    // it, and meant for this API (audience); what the caller may do with it is decided by the
    // authorization policies (AccessPolicies).
    public static AuthenticationBuilder AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, string audience)
    {
        var settings = ApiAuthenticationOptions.From(configuration);
        settings.Validate();

        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = settings.Authority;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;

                // Keep the claim names as Identity issued them ("sub", "role", "scope", "client_id")
                // instead of translating them to long Microsoft URIs, which would make
                // User.FindFirst("sub") and the role checks silently miss.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    RoleClaimType = ClaimNames.Role,
                    NameClaimType = ClaimNames.Name,
                };
            });
    }
}
