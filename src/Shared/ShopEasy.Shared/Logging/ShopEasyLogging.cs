using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace ShopEasy.Shared.Logging;

// One logging setup for every service, instead of six copies of the same block (which had
// drifted: all of them printed "[OrderService]" on the console, whatever service they were).
//
//   Log.Logger = new LoggerConfiguration().ConfigureShopEasy("ShopEasy.OrderService").CreateBootstrapLogger();
//   builder.Host.UseSerilog((ctx, _, cfg) => cfg.ConfigureShopEasy("ShopEasy.OrderService", ctx.Configuration));
public static class ShopEasyLogging
{
    // [12:00:01 INF] [ShopEasy.OrderService] [a1b2c3d4] Order saved
    //  - Service:       which service wrote the line
    //  - CorrelationId: ties the lines of one request together across services and messages
    //                   (see CorrelationContext); "-" outside a request / message
    public const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}";

    public const string SeqUrlKey = "Seq:ServerUrl";
    public const string DefaultSeqUrl = "http://seq:5341";

    // Levels and properties only — no sinks. Split out so the log format can be tested.
    public static LoggerConfiguration ApplyShopEasyDefaults(
        this LoggerConfiguration logger,
        string service,
        string? environment = null)
    {
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // Order matters: enrichers only add a property that is not there yet. The log context
            // (the id CorrelationContext pushes while a request or message is handled) comes
            // first, so the "-" default is only used outside of one.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("CorrelationId", "-")
            .Enrich.WithProperty("Service", service);

        if (!string.IsNullOrEmpty(environment))
            logger.Enrich.WithProperty("Environment", environment);

        return logger;
    }

    // The standard setup: the defaults above, the console, and Seq (unless switched off).
    public static LoggerConfiguration ConfigureShopEasy(
        this LoggerConfiguration logger,
        string service,
        IConfiguration? configuration = null,
        string? environment = null)
    {
        logger.ApplyShopEasyDefaults(service, environment)
              .WriteTo.Console(outputTemplate: ConsoleTemplate);

        var seqUrl = ResolveSeqUrl(configuration);
        if (!string.IsNullOrWhiteSpace(seqUrl))
            logger.WriteTo.Seq(seqUrl, restrictedToMinimumLevel: LogEventLevel.Information);

        return logger;
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
