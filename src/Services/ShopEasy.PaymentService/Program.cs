using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Events;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Logging;
using ShopEasy.Shared.Messaging;
using ShopEasy.Shared.Middleware;
using ShopEasy.Shared.Resilience;
using System.IdentityModel.Tokens.Jwt;


Log.Logger = new LoggerConfiguration()
    .ConfigureShopEasy("ShopEasy.PaymentService")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .ConfigureShopEasy("ShopEasy.PaymentService", ctx.Configuration, ctx.HostingEnvironment.EnvironmentName)
        // unchanged from before: Payment also logs authentication decisions at Information
        .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information));

    builder.Services.AddDbContext<PaymentDbContext>(options =>
        options.UseNpgsql(
            PostgresConnection.FromConfiguration(builder.Configuration)));

    // Prevent ASP.NET from remapping "role" → long URI claim type
    // Without this, [Authorize(Roles = "admin")] never matches
    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
    JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

    builder.Services
        .AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = builder.Configuration["IdentityServer:Authority"]
                ?? "http://identity:8080";
            options.Audience = "shopeasy-api";
            options.RequireHttpsMetadata = false;

            // Assign a NEW object instead of modifying the existing one
            options.TokenValidationParameters = new TokenValidationParameters
            {
                RoleClaimType = "role",
                NameClaimType = "name",
                ValidateAudience = true,
                ValidAudience = "shopeasy-api"
            };
        });

    builder.Services.AddAuthorization();

    // Carry the correlation id across RabbitMQ: stamp it on outgoing messages, restore it (and
    // the OrderId) for the logs of the consumer that receives them
    builder.Services.AddScoped(typeof(CorrelationConsumeFilter<>));
    builder.Services.AddScoped(typeof(CorrelationPublishFilter<>));
    builder.Services.AddScoped(typeof(CorrelationSendFilter<>));

    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<ProcessPaymentConsumer>();
        x.AddConsumer<RefundPaymentConsumer>();

        // ── Transactional inbox/outbox ────────────────────────────────────────
        // Every consumer endpoint below runs inside ONE database transaction that holds the
        // consumer's own changes, an inbox row (so a redelivered message is not processed
        // twice) and the events the consumer publishes (stored in the OutboxMessage table
        // instead of being sent straight away). After the commit MassTransit delivers them to
        // RabbitMQ, so a payment is never recorded without its result event, and an event is
        // never sent for a payment that was rolled back. If the broker is unreachable at that
        // moment, the message stays unacknowledged and its redelivery completes the delivery.
        x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
        {
            o.UsePostgres();
            // Consumers lock and re-read rows; READ COMMITTED lets a waiting transaction see
            // the committed result instead of failing with a serialization error.
            o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
        });

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username is not configured."));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password is not configured."));
            });

            // First, so the correlation scope also covers the retries and the outbox below
            cfg.UseConsumeFilter(typeof(CorrelationConsumeFilter<>), ctx);
            cfg.UsePublishFilter(typeof(CorrelationPublishFilter<>), ctx);
            cfg.UseSendFilter(typeof(CorrelationSendFilter<>), ctx);

            // ── Global retry policy ───────────────────────────────────────────────
            // A database or network outage is ridden out for several minutes with growing
            // pauses; anything else is retried 3 times quickly before moving to the error
            // queue (see ShopEasy.Shared.Resilience.ConsumerResilience, settings under
            // Resilience:Consumers).
            cfg.UseConsumerResilience(ConsumerResilienceOptions.From(builder.Configuration));

            cfg.ReceiveEndpoint("payment-process", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.Bind("ShopEasy.Shared.Contracts:ProcessPaymentCommand", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.UseEntityFrameworkOutbox<PaymentDbContext>(ctx);
                e.ConfigureConsumer<ProcessPaymentConsumer>(ctx);
            });

            cfg.ReceiveEndpoint("payment-refund", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.Bind("ShopEasy.Shared.Contracts:RefundPaymentCommand", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.UseEntityFrameworkOutbox<PaymentDbContext>(ctx);
                e.ConfigureConsumer<RefundPaymentConsumer>(ctx);
            });
        });
    });


    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("ShopEasy.PaymentService"))
            .AddSource("MassTransit") // a span per message published / consumed, linked across RabbitMQ
            .AddSource("Npgsql") // a span per database command
            .AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/health");
            })
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(
                    builder.Configuration["Telemetry:Endpoint"]
                    ?? "http://jaeger:4317");
            }));

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "ShopEasy - Payment API",
            Version = "v1"
        });

        // This adds the Authorize button in Swagger UI
        // so you can paste a Bearer token and test protected endpoints
        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "Paste your JWT token here. Get it from http://localhost:5000/account/token"
        });

        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
    });

    builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<PaymentDbContext>(
        name: "database",
        tags: new[] { "ready" })
    .AddRabbitMQ(
        rabbitConnectionString: $"amqp://{builder.Configuration["RabbitMQ:Username"]}:{builder.Configuration["RabbitMQ:Password"]}@{builder.Configuration["RabbitMQ:Host"]}/",
        name: "rabbitmq",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: new[] { "ready" });

    builder.Services.AddMetricServer(options =>
    {
        options.Port = builder.Configuration.GetValue<ushort>("Metrics:Port", 9091);
    });

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
        options.RoutePrefix = string.Empty; // Swagger UI at root "/"
    });

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();
        try
        {
            logger.LogInformation("Applying database migrations...");
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply migrations");
            throw;
        }
    }

    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (ctx, elapsed, ex) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/health") ||
                ctx.Request.Path.StartsWithSegments("/metrics"))
                return Serilog.Events.LogEventLevel.Debug;
            return Serilog.Events.LogEventLevel.Information;
        };
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseHttpMetrics(options =>
    {
        options.AddCustomLabel("service", _ => "PaymentService"); 
    });
    app.MapMetrics();
    app.MapControllers();

    // Liveness — is the process alive?
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false, // no checks — if process is running, it's live
        ResponseWriter = WriteHealthResponse
    });

    // Readiness — can it serve traffic?
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse
    });

    // Legacy endpoint — keeps docker-compose healthcheck working
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
       .AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Payment Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
static Task WriteHealthResponse(
    HttpContext context,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";
    var result = System.Text.Json.JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            duration = e.Value.Duration.TotalMilliseconds + "ms",
            error = e.Value.Exception?.Message
        }),
        totalDuration = report.TotalDuration.TotalMilliseconds + "ms"
    });
    return context.Response.WriteAsync(result);
}