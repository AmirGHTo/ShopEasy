using MassTransit;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Events;
using ShopEasy.InventoryService.Consumers;
using ShopEasy.InventoryService.Data;
using ShopEasy.InventoryService.Grpc;
using ShopEasy.Shared.Middleware;
using ShopEasy.Shared.Services;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "ShopEasy.InventoryService")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [OrderService] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Seq("http://seq:5341")
    .CreateBootstrapLogger();

try
{
    // Enable HTTP/2 for gRPC before builder is created
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    var builder = WebApplication.CreateBuilder(args);

    // Configure Kestrel to accept both HTTP/1.1 (REST) and HTTP/2 (gRPC)
    builder.WebHost.ConfigureKestrel(options =>
    {
        // REST API — HTTP/1.1 only
        options.ListenAnyIP(8080, o =>
            o.Protocols = HttpProtocols.Http1);

        // gRPC — HTTP/2 only (no TLS negotiation needed when Http2 only)
        options.ListenAnyIP(8090, o =>
            o.Protocols = HttpProtocols.Http2);
    });


    builder.Host.UseSerilog((ctx, _, cfg) => cfg
      .MinimumLevel.Information()
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
      .Enrich.FromLogContext()
      .Enrich.WithProperty("Service", "ShopEasy.InventoryService")
      .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [OrderService] {Message:lj}{NewLine}{Exception}")
      .WriteTo.Seq("http://seq:5341", restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information));

    builder.Services.AddGrpc(options =>
    {
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    });

    builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("ShopEasy.InventoryService"))
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

    builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' not found.")));

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

    // ── Prometheus Metrics ────────────────────────────────────────────────────
    builder.Services.AddMetricServer(options =>
    {
        options.Port = 9091; // separate port from the API
    });

    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<ReserveInventoryConsumer>();
        x.AddConsumer<ReleaseInventoryConsumer>();
        x.AddConsumer<CommitInventoryConsumer>();

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? "guest");
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? "guest");
            });

            // ── Global retry policy ───────────────────────────────────────────────
            // Retry 3 times with exponential backoff before moving to error queue
            cfg.UseMessageRetry(r =>
            {
                r.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(10),
                    intervalDelta: TimeSpan.FromSeconds(2));

                // Don't retry these — they will never succeed
                r.Ignore<ArgumentNullException>();
                r.Ignore<InvalidOperationException>();
            });

            cfg.ReceiveEndpoint("inventory-reserve", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.ConcurrentMessageLimit = 1;
                e.Bind("ShopEasy.Shared.Contracts:ReserveInventoryCommand", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.ConfigureConsumer<ReserveInventoryConsumer>(ctx);
            });

            cfg.ReceiveEndpoint("inventory-release", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.ConcurrentMessageLimit = 1;
                e.Bind("ShopEasy.Shared.Contracts:ReleaseInventoryCommand", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.ConfigureConsumer<ReleaseInventoryConsumer>(ctx);
            });

            cfg.ReceiveEndpoint("inventory-commit", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.ConcurrentMessageLimit = 1;
                e.Bind("ShopEasy.Shared.Contracts:CommitInventoryCommand", b =>
                {
                    b.ExchangeType = "fanout";
                    b.Durable = true;
                });
                e.ConfigureConsumer<CommitInventoryConsumer>(ctx);
            });
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "ShopEasy - Inventory API",
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

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<InventoryDbContext>(
        name: "database",
        tags: new[] { "ready" })
        .AddRabbitMQ(
        rabbitConnectionString: $"amqp://{builder.Configuration["RabbitMQ:Username"]}:{builder.Configuration["RabbitMQ:Password"]}@{builder.Configuration["RabbitMQ:Host"]}/",
        name: "rabbitmq",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: new[] { "ready" });

    builder.Services.AddGrpcHealthChecks()
    .AddCheck("grpc", () =>Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

    #region caching
    var redisConnection = builder.Configuration["Redis:Connection"] ?? "redis:6379";
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "shopeasy:";
    });
    // With this — abortConnect=false means it won't crash if Redis is unavailable:
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>ConnectionMultiplexer.Connect($"{redisConnection},abortConnect=false"));
    builder.Services.AddScoped<CacheService>();
    #endregion



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
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();
        try
        {
            logger.LogInformation("Applying database migrations...");
            db.Database.Migrate();
            // Seed only if no data exists
            if (!db.InventoryItems.Any())
                InventoryDbSeeder.Seed(db);
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
        options.AddCustomLabel("service", _ => "InventoryService");
    });
    app.MapMetrics();
    app.MapControllers();

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
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

    // Map gRPC endpoint — listens on same port as HTTP
    // HTTP/2 is required for gRPC
    app.MapGrpcService<InventoryGrpcService>();
    app.MapGrpcHealthChecksService();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Inventory Service terminated unexpectedly");
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