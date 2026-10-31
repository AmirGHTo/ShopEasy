using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace ShopEasy.Shared.Logging;

// One logging setup for every service, instead of six copies of the same block (which had
// drifted: all of them printed "[OrderService]" on the console, whatever service they were).
//
//   Log.Logger = new LoggerConfiguration().ConfigureShopEasy("ShopEasy.OrderService").CreateBootstrapLogger();
//   builder.Host.UseSerilog((ctx, _, cfg) => cfg.ConfigureShopEasy("ShopEasy.OrderService", ctx.Configuration));
//
// Every line carries the same properties, whatever the output (console text, console JSON, Seq):
//   Service         which service wrote the line
//   ServiceVersion  the commit the image was built from ("dev" outside an image)
//   Environment     Production, Development, ...
//   CorrelationId   ties the lines of one request together across services and messages
//                   (see CorrelationContext); "-" outside a request / message
//   TraceId/SpanId  the span that was active when the line was written, the same ids the
//                   trace in Jaeger has; absent when no span is active
public static class ShopEasyLogging
{
    // [12:00:01 INF] [ShopEasy.OrderService] [a1b2c3d4] [4bf92f3577b34da6a3ce929d0e0e4736] Order saved
    //                                         correlation id   trace id (empty outside a span)
    // {TraceId} is a token Serilog fills in itself from the active span, which is why it is empty
    // there and not "-" like the correlation id.
    public const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [{CorrelationId}] [{TraceId}] {Message:lj}{NewLine}{Exception}";

    // One JSON object per line, the properties above at the top level next to @t (time), @m (message)
    // and @l (level; left out for Information). What Seq, Loki and Elasticsearch ingest as it is.
    public static readonly ITextFormatter JsonFormatter = new RenderedCompactJsonFormatter();

    public const string SeqUrlKey = "Seq:ServerUrl";
    public const string DefaultSeqUrl = "http://seq:5341";

    // Logging:ConsoleFormat (environment variable Logging__ConsoleFormat): "text" or "json".
    public const string ConsoleFormatKey = "Logging:ConsoleFormat";
    public const string TextFormat = "text";
    public const string JsonFormatName = "json";

    // Service:Version (environment variable Service__Version) is baked into the image from the
    // SERVICE_VERSION build argument.
    public const string ServiceVersionKey = "Service:Version";
    public const string DefaultServiceVersion = "dev";

    public const string NoValue = "-";

    // Levels and properties only — no sinks. Split out so the log format can be tested.
    public static LoggerConfiguration ApplyShopEasyDefaults(
        this LoggerConfiguration logger,
        string service,
        string? environment = null,
        string? version = null)
    {
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // Order matters: enrichers only add a property that is not there yet. The log context
            // (the id CorrelationContext pushes while a request or message is handled) comes
            // first, so the "-" default is only used outside of one.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("CorrelationId", NoValue)
            .Enrich.With<ActiveSpanEnricher>()
            .Enrich.WithProperty("Service", service);

        if (!string.IsNullOrEmpty(version))
            logger.Enrich.WithProperty("ServiceVersion", version);

        if (!string.IsNullOrEmpty(environment))
            logger.Enrich.WithProperty("Environment", environment);

        return logger;
    }

    // The standard setup: the defaults above, the console (text or JSON), and Seq (unless switched off).
    public static LoggerConfiguration ConfigureShopEasy(
        this LoggerConfiguration logger,
        string service,
        IConfiguration? configuration = null,
        string? environment = null)
    {
        // The bootstrap logger starts before the configuration exists, so it asks the environment
        // for what the configuration would say.
        environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        logger.ApplyShopEasyDefaults(service, environment, ResolveServiceVersion(configuration));

        // A misspelt format stops the service once it is configured, like every other setting;
        // the bootstrap logger, which has to work to report that, falls back to text.
        if (UsesJson(configuration))
            logger.WriteTo.Console(JsonFormatter);
        else
            logger.WriteTo.Console(outputTemplate: ConsoleTemplate);

        var seqUrl = ResolveSeqUrl(configuration);
        if (!string.IsNullOrWhiteSpace(seqUrl))
            logger.WriteTo.Seq(seqUrl, restrictedToMinimumLevel: LogEventLevel.Information);

        return logger;
    }

    private static bool UsesJson(IConfiguration? configuration)
    {
        try
        {
            return ResolveConsoleFormat(configuration) == JsonFormatName;
        }
        catch (InvalidOperationException) when (configuration is null)
        {
            return false;
        }
    }

    // "text" (default) or "json", case-insensitive. Anything else is an error.
    public static string ResolveConsoleFormat(IConfiguration? configuration)
    {
        var configured = configuration?[ConsoleFormatKey]
                         ?? Environment.GetEnvironmentVariable("Logging__ConsoleFormat");
        if (string.IsNullOrWhiteSpace(configured))
            return TextFormat;

        var format = configured.Trim().ToLowerInvariant();
        return format is TextFormat or JsonFormatName
            ? format
            : throw new InvalidOperationException(
                $"{ConsoleFormatKey} is '{configured}'; it must be '{TextFormat}' or '{JsonFormatName}'.");
    }

    // Service:Version (environment variable Service__Version); "dev" when not set, which is the
    // case when a service runs from source instead of from an image.
    public static string ResolveServiceVersion(IConfiguration? configuration)
    {
        var configured = configuration?[ServiceVersionKey]
                         ?? Environment.GetEnvironmentVariable("Service__Version");
        return string.IsNullOrWhiteSpace(configured) ? DefaultServiceVersion : configured.Trim();
    }

    // Seq:ServerUrl (environment variable Seq__ServerUrl) — empty switches the Seq sink off,
    // for example in a cluster that does not run Seq. The bootstrap logger starts before the
    // configuration exists, so it falls back to the environment variable.
    public static string? ResolveSeqUrl(IConfiguration? configuration)
    {
        var configured = configuration?[SeqUrlKey]
                         ?? Environment.GetEnvironmentVariable("Seq__ServerUrl");
        return configured ?? DefaultSeqUrl;
    }
}

// Adds TraceId and SpanId of the span that is active when the line is written: the span of the
// HTTP request being handled, or of the message being consumed (MassTransit starts one per
// message). They are the ids Jaeger shows, so a line found in the logs leads to its trace and
// a trace leads to its lines. Outside a span it adds nothing.
public sealed class ActiveSpanEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var span = Activity.Current;
        if (span is null || span.IdFormat != ActivityIdFormat.W3C)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", span.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", span.SpanId.ToHexString()));
    }
}
