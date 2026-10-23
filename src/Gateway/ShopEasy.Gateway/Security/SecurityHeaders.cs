using System.Text.RegularExpressions;

namespace ShopEasy.Gateway.Security;

// Settings under "SecurityHeaders".
public sealed class SecurityHeadersSettings
{
    public const string SectionName = "SecurityHeaders";
    public const string DefaultImageSources = "https:";

    // The break-glass switch: false sends none of the headers below. Leave it on.
    public bool Enabled { get; set; } = true;

    // Where the storefront may load product images from, in addition to the gateway itself (the
    // img-src of its Content-Security-Policy). Image addresses come from the catalog, which an
    // administrator edits, so any https address is allowed by default; name your image host(s) to
    // narrow that, e.g. "https://images.example.com". Separated by spaces or commas; each is "https:"
    // or "https://host[:port]" (a host may start with "*."). Empty: only the gateway's own images.
    public string ImageSources { get; set; } = DefaultImageSources;

    public static SecurityHeadersSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<SecurityHeadersSettings>() ?? new SecurityHeadersSettings();

    // A setting ends up inside a header, so it may only hold what it is meant to: nothing that could
    // add a directive or a keyword such as 'unsafe-inline'
    private static readonly Regex Source = new(
        @"^(https:|https://(\*\.)?[a-z0-9]([a-z0-9.-]*[a-z0-9])?(:\d{1,5})?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> ImageSourceList() =>
        ImageSources.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Validate()
    {
        if (ImageSources.Contains(';'))
            throw new InvalidOperationException($"{SectionName}:{nameof(ImageSources)} must not contain ';'.");

        foreach (var source in ImageSourceList())
            if (!Source.IsMatch(source))
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(ImageSources)} contains '{source}', which is not 'https:' or an https address like " +
                    "'https://images.example.com'.");
    }
}

// The headers that tell a browser what a page of the gateway may do. They matter most for the
// storefront (an attacker who gets a script into the page would act as the signed-in customer), and
// they cost nothing on the rest.
//
// The Content-Security-Policy differs by what is being answered:
//   storefront (pages, scripts, styles, images of the shop): scripts and styles only from the gateway's
//     own files, so an injected <script> or inline handler does not run. The build has no inline code.
//   API (/api, /account, health): never rendered as a page, so nothing at all is allowed.
//   operator pages (/portal, /outbox, /sagas, /deadletters): hand-written pages with inline scripts,
//     handlers and styles, so inline code is allowed there, and only there. They are tools for
//     whoever runs the system, not part of the shop.
// Every response, whichever policy it gets, also says: do not guess content types, never be framed,
// send little in the Referer header, and the browser features the shop does not use are off.
public sealed class SecurityHeadersPolicy
{
    private static readonly string[] OperatorPages = ["/portal", "/outbox", "/sagas", "/deadletters"];
    private static readonly string[] ApiPaths = ["/api", "/account", "/health", "/signin-oidc", "/signout-callback-oidc"];

    public string Storefront { get; }
    public string Api { get; }
    public string Operator { get; }

    public SecurityHeadersPolicy(SecurityHeadersSettings settings)
    {
        var images = string.Join(' ', ["'self'", .. settings.ImageSourceList()]);

        Storefront = string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self'",
            $"img-src {images}",
            "font-src 'self'",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");

        Api = "default-src 'none'; frame-ancestors 'none'";

        Operator = string.Join("; ",
            "default-src 'self'",
            "script-src 'self' 'unsafe-inline'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'");
    }

    public string ContentSecurityPolicyFor(PathString path)
    {
        if (Matches(path, OperatorPages)) return Operator;
        if (Matches(path, ApiPaths)) return Api;
        return Storefront;
    }

    private static bool Matches(PathString path, string[] prefixes) =>
        prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}

public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, SecurityHeadersSettings settings)
    {
        var policy = new SecurityHeadersPolicy(settings);

        return app.Use((context, next) =>
        {
            // Set as the response starts, after the reverse proxy has copied the headers of the service
            // behind it: the gateway's policy is the one that counts, whatever a service says
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = policy.ContentSecurityPolicyFor(context.Request.Path);
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY"; // frame-ancestors says it for current browsers, this for old ones
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] =
                    "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            return next(context);
        });
    }
}
