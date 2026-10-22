using Microsoft.AspNetCore.StaticFiles;

namespace ShopEasy.Gateway.Storefront;

// The storefront (src/Web/ShopEasy.Web) is built into wwwroot by the image build and served by the
// gateway itself, so the browser talks to one origin: the session cookie works as it does for the
// APIs and nothing needs CORS.
public static class StorefrontExtensions
{
    private static readonly StaticFileOptions Options = new() { OnPrepareResponse = SetCacheHeaders };

    // The files of the built app, before routing. The build gives every file under /assets a name
    // that changes with its content, so those are cached for good; everything else (index.html)
    // is checked on every visit, so a new release reaches the browser at once.
    public static IApplicationBuilder UseStorefrontFiles(this IApplicationBuilder app)
        => app.UseStaticFiles(Options);

    // Every other page address belongs to the app's own router, so a GET nothing else claims is
    // answered with index.html. A path under /api or /account that nothing handled is a plain 404:
    // an API client must never receive HTML. Endpoints that exist (the proxied APIs, /account/login,
    // /health, ...) win. Returns the fallback, for the caller to add policies.
    public static IEndpointConventionBuilder MapStorefront(this IEndpointRouteBuilder endpoints)
    {
        // An endpoint's Order is compared before how specific its path is, and the proxy routes set
        // their own (the gateway's orders-route has Order 1). These two must come after every one of
        // them, but before the fallback below (which MapFallback ranks at int.MaxValue - 1000).
        endpoints.Map("/api/{**path}", () => Results.NotFound()).AllowAnonymous().After(LastBeforeFallback);
        endpoints.Map("/account/{**path}", () => Results.NotFound()).AllowAnonymous().After(LastBeforeFallback);
        return endpoints.MapFallbackToFile("index.html", Options).AllowAnonymous();
    }

    private const int LastBeforeFallback = int.MaxValue - 2000;

    private static void After(this IEndpointConventionBuilder builder, int order)
        => builder.Add(endpoint => ((RouteEndpointBuilder)endpoint).Order = order);

    private static void SetCacheHeaders(StaticFileResponseContext context)
    {
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/assets")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    }
}
