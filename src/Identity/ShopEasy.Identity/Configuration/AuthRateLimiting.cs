using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ShopEasy.Identity.Configuration;

// Settings under "RateLimiting": how many sign-in and sign-up submissions one client may make per
// window. The per-account lockout stops guessing one password; this stops one client from trying
// many accounts, or from creating accounts in bulk.
public class AuthRateLimitSettings
{
    public const string SectionName = "RateLimiting";

    public Window Login { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
    public Window Registration { get; set; } = new() { PermitLimit = 10, WindowSeconds = 3600 };

    public class Window
    {
        public int PermitLimit { get; set; }
        public int WindowSeconds { get; set; }
    }

    public static AuthRateLimitSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<AuthRateLimitSettings>() ?? new AuthRateLimitSettings();

    public void Validate()
    {
        Check(nameof(Login), Login);
        Check(nameof(Registration), Registration);
    }

    private static void Check(string name, Window window)
    {
        if (window.PermitLimit < 1 || window.WindowSeconds < 1)
            throw new InvalidOperationException(
                $"{SectionName}:{name} needs a PermitLimit and WindowSeconds of at least 1.");
    }
}

public static class AuthRateLimiting
{
    private const string LoginPath = "/Account/Login";
    private const string RegisterPath = "/Account/Register";

    // Only the submissions are limited (POST), per client address; everything else is untouched.
    // Behind a proxy the address is the client's only when ForwardedHeaders is configured.
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(AuthRateLimitSettings settings) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var (name, window) = Select(context.Request, settings);
            if (window is null)
                return RateLimitPartition.GetNoLimiter("unlimited");

            return RateLimitPartition.GetFixedWindowLimiter($"{name}:{Client(context)}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = window.PermitLimit,
                Window = TimeSpan.FromSeconds(window.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });

    private static (string Name, AuthRateLimitSettings.Window? Window) Select(HttpRequest request, AuthRateLimitSettings settings)
    {
        if (!HttpMethods.IsPost(request.Method))
            return ("", null);
        if (request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
            return ("login", settings.Login);
        if (request.Path.Equals(RegisterPath, StringComparison.OrdinalIgnoreCase))
            return ("register", settings.Registration);
        return ("", null);
    }

    // An IPv6 user owns a whole /64, so one address would let them dodge the limit by rotating
    internal static string Client(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, AuthRateLimitSettings settings)
    {
        settings.Validate();
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = CreateLimiter(settings);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ShopEasy.Identity.RateLimiting")
                    .LogWarning("Too many {Method} requests to {Path} from {Client}", http.Request.Method, http.Request.Path, Client(http));

                http.Response.ContentType = "text/plain";
                await http.Response.WriteAsync("Too many attempts. Please wait a while and try again.", cancellationToken);
            };
        });
        return services;
    }
}
