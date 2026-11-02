using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ShopEasy.Shared.Logging;

namespace ShopEasy.Shared.Telemetry;

// What a service says about its traces, from configuration (appsettings.json, or the environment
// variables Telemetry__Endpoint and Telemetry__SamplingRatio). A value that makes no sense stops the
// service at start-up, like every other setting.
public sealed record TelemetrySettings(string? Endpoint, double SamplingRatio)
{
    public const string EndpointKey = "Telemetry:Endpoint";
    public const string SamplingRatioKey = "Telemetry:SamplingRatio";

    // The OpenTelemetry Collector of the Compose stack. The services do not know Jaeger: where the
    // spans end up is decided in the Collector's configuration.
    public const string DefaultEndpoint = "http://otel-collector:4317";

    // Keep every trace. A busy system keeps a fraction (0.1 is a usual start).
    public const double DefaultSamplingRatio = 1.0;

    public static TelemetrySettings From(IConfiguration configuration)
    {
        // Not set: the default. Set but empty: no export, as with Seq (a cluster without a collector).
        var endpoint = configuration[EndpointKey] ?? DefaultEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = null;
        else if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                 || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{EndpointKey} is '{endpoint}'; it must be an http:// or https:// address, or empty to switch the export off.");
        else
            endpoint = uri.ToString();

        var ratio = DefaultSamplingRatio;
        var configured = configuration[SamplingRatioKey];
        if (!string.IsNullOrWhiteSpace(configured)
            && (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)
                || double.IsNaN(ratio) || ratio < 0 || ratio > 1))
            throw new InvalidOperationException(
                $"{SamplingRatioKey} is '{configured}'; it must be a number from 0 to 1 (1 keeps every trace, 0.1 one in ten).");

        return new TelemetrySettings(endpoint, ratio);
    }
}

// The tracing every service shares, so that six copies cannot drift apart:
//
//   builder.Services.AddShopEasyTracing(builder.Configuration, "ShopEasy.OrderService",
//       builder.Environment.EnvironmentName, t => t.AddSource("MassTransit"));
//
// What it sets up: spans for incoming HTTP requests (not /health) and outgoing HTTP calls and for the
// database commands (Npgsql); what each trace says about the service that wrote it (service.name,
// service.version, deployment.environment, service.instance.id); a sampler; and the OTLP export to the
// Collector. A service adds what only it has (the message bus, Redis, gRPC) in the callback.
//
// Spans exist, and so do the trace and span ids in the log lines, whatever the sampling ratio and
// even with the export switched off.
public static class Tracing
{
    public static IServiceCollection AddShopEasyTracing(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        string environmentName,
        Action<TracerProviderBuilder>? configure = null)
    {
        var settings = TelemetrySettings.From(configuration);
        var resource = CreateResource(serviceName, ShopEasyLogging.ResolveServiceVersion(configuration), environmentName);

        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resource)
                .SetSampler(CreateSampler(settings.SamplingRatio))
                .AddSource("Npgsql") // a span per database command
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation();

            configure?.Invoke(tracing);

            if (settings.Endpoint is not null)
                tracing.AddOtlpExporter(options => options.Endpoint = new Uri(settings.Endpoint));
        });

        return services;
    }

    // Which release, which environment and which copy wrote a trace. The values an operator sets in
    // OTEL_RESOURCE_ATTRIBUTES win over these (the detector comes last).
    public static ResourceBuilder CreateResource(string serviceName, string serviceVersion, string environmentName) =>
        ResourceBuilder.CreateEmpty()
            .AddTelemetrySdk()
            .AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: Environment.MachineName)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environmentName)])
            .AddEnvironmentVariableDetector();

    // Parent-based: the first service of a trace decides, and every later one follows, so a trace is
    // never kept by half of the services it passes through. Only a span without a parent (the start
    // of a trace) is decided by the ratio.
    public static Sampler CreateSampler(double ratio) =>
        new ParentBasedSampler(ratio switch
        {
            >= 1 => new AlwaysOnSampler(),
            <= 0 => new AlwaysOffSampler(),
            _ => new TraceIdRatioBasedSampler(ratio)
        });
}
