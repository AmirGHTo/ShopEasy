using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace ShopEasy.Identity.Configuration;

// An account that signed in with a temporary password has to choose its own before it can do
// anything else. The requirement is a claim on the account, so it needs no schema change, and the
// sign-in cookie carries it, so checking it costs no database query.
public static class MustChangePassword
{
    public const string ClaimType = "must_change_password";

    public static Claim NewClaim() => new(ClaimType, "true");

    public static bool IsSet(ClaimsPrincipal user) => user.HasClaim(ClaimType, "true");
}

// Until the password is changed, a signed-in account only reaches the change-password page, sign-out
// and the end-session endpoint: everything else, including the authorize endpoint that would hand
// the application a token, sends it to the change-password page first and back afterwards.
// Runs before IdentityServer's own middleware, which answers the protocol endpoints itself.
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private const string ChangePasswordPath = "/Account/ChangePassword";

    private static readonly PathString[] Open =
    [
        new(ChangePasswordPath), new("/Account/Logout"), new("/connect/endsession"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsOpen(context.Request.Path))
        {
            var session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (session.Succeeded && MustChangePassword.IsSet(session.Principal!))
            {
                var back = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
                context.Response.Redirect($"{ChangePasswordPath}?returnUrl={back}");
                return;
            }
        }
        await next(context);
    }

    private static bool IsOpen(PathString path) => Open.Any(o => path.StartsWithSegments(o, StringComparison.OrdinalIgnoreCase));
}
