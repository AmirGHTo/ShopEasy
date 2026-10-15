using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ShopEasy.Shared.Security;

// Settings under "Security" for the services a browser talks to (the gateway and Identity).
public class WebSecurityOptions
{
    public const string SectionName = "Security";

    // When the session and login cookies get the Secure flag, which keeps a browser from ever
    // sending them over plain HTTP:
    //   Always        - on every response. Use this for a deployment reached over HTTPS.
    //   SameAsRequest - only when the request itself arrived over HTTPS (so a local http://localhost
    //                   demo still works). Behind a TLS-terminating proxy this needs
    //                   ForwardedHeaders, otherwise the service sees plain HTTP and issues cookies
    //                   without the flag.
    //   None          - never.
    public CookieSecurePolicy CookieSecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;

    public static WebSecurityOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<WebSecurityOptions>() ?? new WebSecurityOptions();
}

// Settings under "ForwardedHeaders": believing the X-Forwarded-* headers of a reverse proxy or
// ingress controller in front of the service. Without them a service behind TLS termination sees
// plain HTTP and the proxy's address: it builds http:// redirect URIs for the login, issues cookies
// without the Secure flag and rate-limits the proxy instead of the client.
public class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public bool Enabled { get; set; }

    // The only senders whose headers are believed, as CIDR ranges ("10.0.0.0/8, 172.16.0.0/12")
    // and/or single addresses. Required when Enabled: believing everybody would let any client
    // claim any address and scheme.
    public string KnownNetworks { get; set; } = "";
    public string KnownProxies { get; set; } = "";

    public static ForwardedHeadersSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();

    internal static IEnumerable<string> Split(string list) =>
        list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Validate()
    {
        if (!Enabled)
            return;
        var networks = Split(KnownNetworks).ToList();
        var proxies = Split(KnownProxies).ToList();
        if (networks.Count == 0 && proxies.Count == 0)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Enabled)} is on but neither {SectionName}:{nameof(KnownNetworks)} nor " +
                $"{SectionName}:{nameof(KnownProxies)} lists the proxy to believe.");

        foreach (var network in networks)
            Parse(network);
        foreach (var proxy in proxies)
            if (!IPAddress.TryParse(proxy, out _))
                throw new InvalidOperationException($"{SectionName}:{nameof(KnownProxies)} contains '{proxy}', which is not an IP address.");
    }

    internal static (IPAddress Prefix, int Length) Parse(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length)
            || length < 0 || length > (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(KnownNetworks)} contains '{cidr}', which is not a network like 10.0.0.0/8.");
        return (prefix, length);
    }
}

public static class WebSecurityExtensions
{
    // Registers the forwarded-headers handling. Call UseConfiguredForwardedHeaders first in the
    // pipeline.
    public static IServiceCollection AddConfiguredForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = ForwardedHeadersSettings.From(configuration);
        settings.Validate();
        if (!settings.Enabled)
            return services;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            // only the listed senders are believed (the defaults, loopback, are replaced)
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in ForwardedHeadersSettings.Split(settings.KnownNetworks))
            {
                var (prefix, length) = ForwardedHeadersSettings.Parse(network);
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
            }
            foreach (var proxy in ForwardedHeadersSettings.Split(settings.KnownProxies))
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        return services;
    }

    public static IApplicationBuilder UseConfiguredForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (ForwardedHeadersSettings.From(configuration).Enabled)
            app.UseForwardedHeaders();
        return app;
    }
}
