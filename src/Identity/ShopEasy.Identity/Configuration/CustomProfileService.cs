using System.Security.Claims;
using Duende.IdentityServer.AspNetIdentity;
using Duende.IdentityServer.Models;
using Microsoft.AspNetCore.Identity;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Configuration;

// This is called by IdentityServer when building the token payload.
// It lets you inject custom claims (like role, name) into the tokens.
public class CustomProfileService : ProfileService<ApplicationUser>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CustomProfileService(
        UserManager<ApplicationUser> userManager,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory)
        : base(userManager, claimsFactory)
    {
        _userManager = userManager;
    }

    // Called when IdentityServer is building the claims for a token
    protected override async Task GetProfileDataAsync(
        ProfileDataRequestContext context,
        ApplicationUser user)
    {
        await base.GetProfileDataAsync(context, user);

        var roles = await _userManager.GetRolesAsync(user);

        // Add role claims to the token
        // Services will read User.IsInRole("admin") or User.FindFirst("role")
        var roleClaims = roles.Select(r => new Claim("role", r));
        context.IssuedClaims.AddRange(roleClaims);

        // Add name claims so the frontend knows who is logged in
        context.IssuedClaims.Add(new Claim("given_name", user.FirstName));
        context.IssuedClaims.Add(new Claim("family_name", user.LastName));
        context.IssuedClaims.Add(new Claim("email", user.Email!));
    }
}
