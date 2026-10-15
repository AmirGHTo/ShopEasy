using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopEasy.Shared.Security;

namespace ShopEasy.UnitTests.Support;

// One API as the services start it: the same bearer-token validation and the same policies
// (ApiAuthentication / AccessPolicies from ShopEasy.Shared), the real controllers of the service,
// listening on a free local port. Tokens come from TestIdentityProvider.
//
// A controller's own dependencies (database, message bus) are not registered, so a request that
// gets past authorization fails inside the controller with 500. That is on purpose: the tests ask
// "was the caller let in?", and 401/403 are decided before the controller exists.
internal sealed class AuthHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public string Url { get; }
    public IReadOnlyList<(string Method, string Pattern)> Endpoints { get; }

    private AuthHost(WebApplication app, HttpClient client, IReadOnlyList<(string, string)> endpoints)
    {
        _app = app;
        Client = client;
        Url = app.Urls.First();
        Endpoints = endpoints;
    }

    static AuthHost()
    {
        // cleartext HTTP/2 for the gRPC tests
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    public static async Task<AuthHost> StartAsync(
        string audience,
        IEnumerable<AccessPolicy> policies,
        Assembly? controllers = null,
        Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? map = null,
        bool http2 = false,
        Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));

        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>
        {
            ["IdentityServer:Authority"] = TestIdentityProvider.Issuer,
        });

        builder.Services.AddApiAuthentication(builder.Configuration, audience);
        builder.Services.AddApiAuthorization(policies);

        // the discovery document and signing key Identity would provide
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = TestIdentityProvider.ConfigurationManager());

        if (controllers is not null)
            builder.Services.AddControllers().AddApplicationPart(controllers);
        configure?.Invoke(builder);

        var app = builder.Build();

        // a controller that cannot be built (see above) ends as a plain 500
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (Exception) when (!context.Response.HasStarted) { context.Response.StatusCode = StatusCodes.Status500InternalServerError; }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        if (controllers is not null)
            app.MapControllers();
        map?.Invoke(app);

        await app.StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (method, endpoint.RoutePattern.RawText ?? "")))
            .ToList();

        return new AuthHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) }, endpoints);
    }

    // The request with an optional bearer token
    public async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
