using Microsoft.Extensions.Options;
using ShopEasy.Shared.Resilience;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace ShopEasy.Gateway.Resilience;

public static class GatewayResilienceExtensions
{
    // Adds to the reverse proxy: a timeout for a service that goes silent, limits on how connections
    // to the services are opened and reused, and retries of GET requests after a connection failure.
    // Settings: Resilience:Gateway and Resilience:RetryBudget.
    public static IReverseProxyBuilder AddGatewayResilience(
        this IReverseProxyBuilder proxy, IConfiguration configuration)
    {
        var services = proxy.Services;

        services.AddOptions<GatewayResilienceOptions>()
            .Bind(configuration.GetSection(GatewayResilienceOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        services.AddOptions<RetryBudgetOptions>()
            .Bind(configuration.GetSection(RetryBudgetOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();

        services.AddSingleton(sp => new RetryBudget(sp.GetRequiredService<IOptions<RetryBudgetOptions>>().Value));

        var settings = configuration.GetSection(GatewayResilienceOptions.SectionName).Get<GatewayResilienceOptions>()
            ?? new GatewayResilienceOptions();
        settings.Validate();

        // the timeout is applied to every cluster as the configuration is loaded
        services.AddSingleton<IProxyConfigFilter, ServiceTimeoutFilter>();

        proxy.ConfigureHttpClient((_, handler) =>
        {
            handler.ConnectTimeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds);
            handler.PooledConnectionLifetime = TimeSpan.FromSeconds(settings.PooledConnectionLifetimeSeconds);
        });

        // the factory YARP uses to create the client of each cluster, with the retry handler added
        services.AddSingleton<IForwarderHttpClientFactory, ResilientForwarderHttpClientFactory>();

        return proxy;
    }

}

// Gives every cluster the activity timeout from the settings, unless the cluster configures its own
public sealed class ServiceTimeoutFilter(IOptions<GatewayResilienceOptions> options) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancellationToken)
    {
        var request = cluster.HttpRequest ?? new ForwarderRequestConfig();
        if (request.ActivityTimeout is not null) return ValueTask.FromResult(cluster);

        return ValueTask.FromResult(cluster with
        {
            HttpRequest = request with { ActivityTimeout = TimeSpan.FromSeconds(options.Value.ActivityTimeoutSeconds) }
        });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route, ClusterConfig? cluster, CancellationToken cancellationToken) =>
        ValueTask.FromResult(route);
}

// YARP's own client factory, with the GET retry handler wrapped around the handler it builds
public sealed class ResilientForwarderHttpClientFactory(
    IOptions<GatewayResilienceOptions> options, RetryBudget budget) : ForwarderHttpClientFactory
{
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler) =>
        new SafeRetryHandler(base.WrapHandler(context, handler), budget, options.Value.GetRetries);
}
