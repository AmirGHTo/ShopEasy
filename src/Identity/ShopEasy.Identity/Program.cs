using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Events;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Middleware;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "ShopEasy.Identity")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [OrderService] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Seq("http://seq:5341")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ShopEasy Identity Server...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "ShopEasy.Identity")
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [OrderService] {Message:lj}{NewLine}{Exception}")
        .WriteTo.Seq("http://seq:5341", restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information));

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' not found.")));

    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.Password.RequireDigit           = true;
            options.Password.RequireLowercase        = true;
            options.Password.RequireUppercase        = true;
            options.Password.RequireNonAlphanumeric  = false;
            options.Password.RequiredLength          = 8;
            options.User.RequireUniqueEmail          = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

    builder.Services.AddMetricServer(options =>
    {
        options.Port = 9091; // separate port from the API
    });

    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.Cookie.HttpOnly = true;
    });
    builder.Services.ConfigureExternalCookie(options =>
    {
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    });


    builder.Services.Configure<CookiePolicyOptions>(options =>
    {
        options.MinimumSameSitePolicy = SameSiteMode.Lax;
        options.Secure = CookieSecurePolicy.None;
    });

    // ── Duende IdentityServer ────────────────────────────────────────────────
    builder.Services
        .AddIdentityServer(options =>
        {
            options.Events.RaiseErrorEvents       = true;
            options.Events.RaiseInformationEvents = true;
            options.Events.RaiseFailureEvents     = true;
            options.Events.RaiseSuccessEvents     = true;
            options.EmitStaticAudienceClaim       = true;
        })
        .AddInMemoryIdentityResources(IdentityServerConfig.IdentityResources)
        .AddInMemoryApiScopes(IdentityServerConfig.ApiScopes)
        .AddInMemoryApiResources(IdentityServerConfig.ApiResources)
        .AddInMemoryClients(IdentityServerConfig.GetClients(builder.Configuration))
        .AddAspNetIdentity<ApplicationUser>()
        .AddProfileService<CustomProfileService>();
    // Note: AddDeveloperSigningCredential is for development only.
    // In production, use .AddSigningCredential() with a real certificate.

    builder.Services.AddRazorPages();
    builder.Services.AddControllersWithViews();

    builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>(
        name: "database",
        tags: new[] { "ready" });



    builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("ShopEasy.Identity"))
        .AddAspNetCoreInstrumentation(options =>
        {
            options.Filter = ctx =>
                !ctx.Request.Path.StartsWithSegments("/health");
        })
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://jaeger:4317");
        }));

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<Program>>();
        try
        {
            logger.LogInformation("Applying database migrations...");
            db.Database.Migrate();
            // Demo users are opt-in (Seed:Enabled=true) — never seed known credentials in production
            if (app.Configuration.GetValue<bool>("Seed:Enabled"))
                await DatabaseSeeder.SeedUsersAsync(scope.ServiceProvider);
            logger.LogInformation("Database migrations applied.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply migrations");
            throw;
        }
    }

    app.UseHttpMetrics(options =>
    {
        options.AddCustomLabel("service", _ => "InventoryService");
    });
    app.MapMetrics();

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
    app.UseStaticFiles();
    app.UseRouting();
    app.UseCookiePolicy();
    app.UseIdentityServer(); // Includes UseAuthentication()
    app.UseAuthorization();
    app.MapRazorPages().RequireAuthorization();

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
    Log.Fatal(ex, "Identity Server terminated unexpectedly");
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