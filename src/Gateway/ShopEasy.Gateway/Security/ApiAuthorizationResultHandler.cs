using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ShopEasy.Gateway.Security;

// What the gateway answers a request without a session, when the address needs one.
//
// A page address (/sagas, /outbox, ...) sends the browser to Identity to sign in: that is the
// OpenID Connect redirect, and it is right for a person who typed or clicked the address.
//
// A call under /api comes from a script, and a script cannot take part in a sign-in: the storefront
// calls it with fetch, the browser follows the redirect to Identity, which is another origin that
// does not allow it, and the page only learns "Failed to fetch". So these get what HTTP has for
// "who are you": 401. The caller can then show its own sign-in link, which is what the storefront does.
//
// This runs where the authorization middleware has decided, before any sign-in is started, so a 401
// leaves nothing behind (starting a sign-in would also set a correlation cookie on each answer).
// Everything else, 403 for a signed-in user who may not, is left to the default handler.
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "not_signed_in",
                message = "Sign in to continue."
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, result);
    }
}
