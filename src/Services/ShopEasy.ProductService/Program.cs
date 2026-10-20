using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Events;
using ShopEasy.ProductService.Data;
using ShopEasy.Shared.Logging;
using ShopEasy.Shared.Middleware;
using ShopEasy.Shared.Resilience;
using ShopEasy.Shared.Security;
using ShopEasy.Shared.Services;

Log.Logger = new LoggerConfiguration()
    .ConfigureShopEasy("ShopEasy.ProductService")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .ConfigureShopEasy("ShopEasy.ProductService", ctx.Configuration, ctx.HostingEnvironment.EnvironmentName));

    builder.Services.AddDbContext<ProductDbContext>(options =>
        options.UseNpgsql(
            PostgresConnection.FromConfiguration(builder.Configuration)));

    // ── JWT Authentication ────────────────────────────────────────────────────
    // This middleware:
    // 1. Reads the Authorization: Bearer <token> header
    // 2. Fetches public keys from the IdP's /.well-known/jwks endpoint (cached)
    // 3. Validates the signature, expiry, issuer, and audience
    // 4. Populates HttpContext.User with claims from the token

    // Bearer tokens issued by Identity for this API, and the scope/role policies of its endpoints
    // (anything not marked otherwise requires a signed-in caller)
    builder.Services.AddApiAuthentication(builder.Configuration, ApiAudiences.Product);
    builder.Services.AddApiAuthorization(AccessPolicies.Product);

    builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService("ShopEasy.ProductService"))
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
            Title = "ShopEasy Product API",
            Version = "v1",
            Description = "Original product API — stable, no breaking changes"
        });
        options.SwaggerDoc("v2", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "ShopEasy Product API",
            Version = "v2",
            Description = "Enhanced product API — adds currency, formatted price, createdAt"
        });

        options.DocInclusionPredicate((docName, apiDesc) =>
        {
            var actionApiVersionModel = apiDesc.ActionDescriptor
                .EndpointMetadata
                .OfType<Asp.Versioning.ApiVersionAttribute>()
                .FirstOrDefault();

            if (actionApiVersionModel == null)
                return docName == "v1"; // unversioned endpoints go to v1

            return actionApiVersionModel.Versions
                .Any(v => $"v{v.MajorVersion}" == docName);
        });

        // ← Resolve conflict when two controllers have same method names
        options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

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
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
    });
    builder.Services.AddApiVersioning(options =>
    {
        // Default version when client doesn't specify
        options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;

        // Return API version info in response headers
        // X-Api-Version: 1.0
        options.ReportApiVersions = true;

        // Support both URL segment and header versioning
        options.ApiVersionReader = Asp.Versioning.ApiVersionReader.Combine(
            new Asp.Versioning.UrlSegmentApiVersionReader(),
            new Asp.Versioning.HeaderApiVersionReader("X-Api-Version")
        );
    })
    .AddApiExplorer(options =>
    {
        // Format version as 'v1', 'v2' in URL
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });


    // ── Redis Distributed Cache ───────────────────────────────────────────────
    // Caches product data to reduce database load.
    // TTL: 5 minutes for product lists, 5 minutes for individual products.
    // Cache is invalidated when products are created, updated, or deleted.
    // Redis:Connection and the timeouts are settings (README, pattern 16): a slow Redis costs a
    // request a fraction of a second, then it reads the database.
    builder.Services.AddRedisCache(builder.Configuration);

    builder.Services.AddScoped<CacheService>();

    builder.Services
        .AddHealthChecks()
        .AddDbContextCheck<ProductDbContext>(
            name: "database",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            tags: new[] { "ready" })
        .AddUrlGroup(
            uri: new Uri($"{builder.Configuration["IdentityServer:Authority"]}/health"),
            name: "identity",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            tags: new[] { "ready" });

    // ── Prometheus Metrics ────────────────────────────────────────────────────
    builder.Services.AddMetricServer(options =>
    {
        // separate port from the API (configurable so two services can share a host)
        options.Port = builder.Configuration.GetValue<ushort>("Metrics:Port", 9091);
    });

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Products API v1");
        options.SwaggerEndpoint("/swagger/v2/swagger.json", "Products API v2");
        options.RoutePrefix = string.Empty; // Swagger UI at root "/"
    });


    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();
        try
        {
            logger.LogInformation("Applying database migrations...");
            db.Database.Migrate();
            if (!db.Products.Any())
                ProductDbSeeder.Seed(db);
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
        options.AddCustomLabel("service", _ => "ProductService");
    });
    // scraped by Prometheus without credentials (see "Known limitations" in the README)
    app.MapMetrics().AllowAnonymous();
    app.MapControllers();

    // Liveness — is the process alive?
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false, // no checks — if process is running, it's live
        ResponseWriter = WriteHealthResponse
    }).AllowAnonymous();

    // Readiness — can it serve traffic?
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse
    }).AllowAnonymous();

    // Legacy endpoint — keeps docker-compose healthcheck working
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
       .AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Product Service terminated unexpectedly");
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
