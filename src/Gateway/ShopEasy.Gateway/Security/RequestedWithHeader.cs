namespace ShopEasy.Gateway.Security;

// The gateway's session is a cookie, and a browser attaches it to every request to the gateway,
// whichever page started the request. SameSite=Lax keeps it off cross-site POSTs, and this is the
// second, independent defence against a page that makes the browser change something on the
// user's behalf (cross-site request forgery): every request to /api that changes state must carry
// an X-Requested-With header. A page of another origin cannot add a header of its own without a
// CORS preflight, and the gateway answers none, so only the shop's own pages can send it.
//
// The value does not matter, only that the header is there. Reading (GET, HEAD, OPTIONS) needs
// nothing; the sign-in and sign-out addresses are plain GETs and are not under /api.
public static class RequestedWithHeader
{
    public const string Name = "X-Requested-With";

    public static IApplicationBuilder UseRequestedWithHeaderCheck(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (ChangesState(request.Method)
                && request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(request.Headers[Name]))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "request_header_missing",
                    message = $"A request that changes something must carry the {Name} header."
                });
                return;
            }

            await next(context);
        });

    private static bool ChangesState(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
}
