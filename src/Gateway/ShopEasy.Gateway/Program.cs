using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Events;
using ShopEasy.Shared.Logging;
using ShopEasy.Shared.Messaging;
using ShopEasy.Shared.Middleware;
using ShopEasy.Shared.Security;
using System.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;
using ShopEasy.Gateway.Resilience;
using ShopEasy.Gateway.Security;
using ShopEasy.Gateway.Storefront;

Log.Logger = new LoggerConfiguration()
    .ConfigureShopEasy("ShopEasy.Gateway")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .ConfigureShopEasy("ShopEasy.Gateway", ctx.Configuration, ctx.HostingEnvironment.EnvironmentName));

    var authSettings = ApiAuthenticationOptions.From(builder.Configuration);
    authSettings.Validate();
    var identityAuthority = authSettings.Authority;
    var webSecurity = WebSecurityOptions.From(builder.Configuration);
    var securityHeaders = SecurityHeadersSettings.From(builder.Configuration);
    securityHeaders.Validate();
    var rateLimits = GatewayRateLimitSettings.From(builder.Configuration);
    rateLimits.Validate();

    // Believe the X-Forwarded-* headers of the ingress in front of the gateway, if one is configured
    builder.Services.AddConfiguredForwardedHeaders(builder.Configuration);

    // 401 (not the redirect to Identity) for a call under /api without a session
    builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

    // Renewal of the access token in the session (see AccessTokenRenewal)
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IAccessTokenRefresher, AccessTokenRefresher>();
    builder.Services.AddHttpClient(AccessTokenRefresher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
    // Browser-facing URL of the IdP (the Authority is the internal Docker/k8s address)
    var identityPublicUrl = builder.Configuration["IdentityServer:PublicUrl"] ?? "http://localhost:5001";

    // ── Authentication: OIDC + Cookie (BFF pattern) ───────────────────────────
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = "oidc";
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "shopeasy.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = webSecurity.CookieSecurePolicy;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            };

            // renew the access token before it runs out (it lives an hour, the session up to 30 days)
            options.Events.OnValidatePrincipal = AccessTokenRenewal.ValidateAsync;
        })
        .AddOpenIdConnect("oidc", options =>
        {
            options.Authority = identityAuthority;
            options.ClientId = "shopeasy-gateway";
            options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]
                ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");
            options.ResponseType = "code";
            options.RequireHttpsMetadata = authSettings.RequireHttpsMetadata;

            // What the gateway may ask for on behalf of the signed-in user; the services demand the
            // matching scope on each endpoint (ShopEasy.Shared.Security.AccessPolicies)
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            foreach (var scope in ApiScopes.Web)
                options.Scope.Add(scope);
            options.Scope.Add("offline_access");

            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "role";

            // Keep the claim names exactly as the identity server sends them ("sub", "role", ...)
            // instead of translating them to long Microsoft URIs. The rate limiter policies and
            // /account/me look for "sub" and "role"; with the default mapping the user id was
            // missing, so per-user limits silently fell back to the client IP.
            options.MapInboundClaims = false;

            // The roles only come back from the userinfo endpoint (they are not in the ID token)
            // and are not copied into the login cookie by default. Without this the gateway
            // never knew a user was an admin (IsInRole("admin") was always false).
            options.ClaimActions.MapJsonKey("role", "role");

            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";

            options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.IssuerAddress =
                        ctx.ProtocolMessage.IssuerAddress
                            .Replace(identityAuthority, identityPublicUrl);
                    return Task.CompletedTask;
                },
                OnRedirectToIdentityProviderForSignOut = ctx =>
                {
                    ctx.ProtocolMessage.IssuerAddress =
                        ctx.ProtocolMessage.IssuerAddress
                            .Replace(identityAuthority, identityPublicUrl);
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();

    // ── Prometheus Metrics ────────────────────────────────────────────────────
    builder.Services.AddMetricServer(options =>
    {
        // separate port from the API (configurable so two services can share a host)
        options.Port = builder.Configuration.GetValue<ushort>("Metrics:Port", 9091);
    });

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    // Protects all downstream services from abuse at the Gateway level.
    // Uses fixed window algorithm — simple and effective for most cases.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // The limits (README, "Rate Limiting"): anonymous, authenticated and orders
        options.AddGatewayPolicies(rateLimits);

        // ── Response when rate limit exceeded ─────────────────────────────────
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            context.HttpContext.Response.ContentType = "application/json";

            var retryAfter = context.Lease.TryGetMetadata(
                System.Threading.RateLimiting.MetadataName.RetryAfter,
                out var retryAfterValue)
                ? (int)retryAfterValue.TotalSeconds
                : 60;

            context.HttpContext.Response.Headers["Retry-After"] = retryAfter.ToString();

            await context.HttpContext.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "Too many requests",
                    message = $"Rate limit exceeded. Try again in {retryAfter} seconds.",
                    retryAfter = retryAfter
                }), ct);
        };
    });

    builder.Services
        .AddHealthChecks()
        .AddUrlGroup(
            uri: new Uri("http://identity:8080/health"),
            name: "identity",
            failureStatus: HealthStatus.Degraded,
            tags: new[] { "ready" })
        .AddUrlGroup(
            uri: new Uri("http://product:8080/health/ready"),
            name: "product-service",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" })
        .AddUrlGroup(
            uri: new Uri("http://order:8080/health/ready"),
            name: "order-service",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" })
        .AddUrlGroup(
            uri: new Uri("http://inventory:8080/health/ready"),
            name: "inventory-service",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" })
        .AddUrlGroup(
            uri: new Uri("http://payment:8080/health/ready"),
            name: "payment-service",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });

    // ── OpenTelemetry Distributed Tracing ─────────────────────────────────────
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService("ShopEasy.Gateway"))
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

    // ── YARP Reverse Proxy ────────────────────────────────────────────────────
    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
        .AddTransforms(transforms =>
        {
            transforms.AddRequestTransform(async ctx =>
            {
                var token = await ctx.HttpContext.GetTokenAsync("access_token");
                if (!string.IsNullOrEmpty(token))
                {
                    ctx.ProxyRequest.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                }

                // The id of this request — the client's, or the one the middleware generated —
                // goes to the service behind the gateway, so its logs and messages carry it too
                CorrelationContext.Propagate(ctx.ProxyRequest);
            });
        })
        // Timeout for a silent service, connection limits, and retries of GETs after a connection
        // failure (settings: Resilience:Gateway and Resilience:RetryBudget)
        .AddGatewayResilience(builder.Configuration);

    var app = builder.Build();

    // first, so that everything after it sees the client's address and the original scheme
    app.UseConfiguredForwardedHeaders(builder.Configuration);

    app.UseMiddleware<CorrelationIdMiddleware>();

    // Early, so that every answer has the headers, also the ones refused or redirected below
    if (securityHeaders.Enabled)
        app.UseSecurityHeaders(securityHeaders);

    // Before anything is authenticated or proxied: a state-changing /api request without the
    // header never gets that far
    app.UseRequestedWithHeaderCheck();

    // The storefront's files; before the metrics, so serving a script is not counted as a request
    app.UseStorefrontFiles();

    app.UseHttpMetrics(options =>
    {
        // Track HTTP request metrics automatically
        options.AddCustomLabel("service", _ => "Gateway");
    });
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
    // After authentication so the "authenticated"/"orders" policies can partition by user
    app.UseRateLimiter();

    #region pages 

    // Auth endpoints — anonymous limit (login page)
    app.MapGet("/account/login", async (HttpContext ctx, string? returnUrl) =>
    {
        var target = LocalReturnUrl.OrHome(returnUrl);
        if (ctx.User.Identity?.IsAuthenticated == true)
            return Results.Redirect(target);

        await ctx.ChallengeAsync("oidc", new AuthenticationProperties
        {
            RedirectUri = target
        });
        return Results.Empty;
    }).AllowAnonymous().RequireRateLimiting("anonymous");
    app.MapGet("/account/logout", async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignOutAsync("oidc", new AuthenticationProperties
        {
            RedirectUri = "/"
        });
    }).RequireAuthorization().RequireRateLimiting("authenticated");
    app.MapGet("/account/me", (HttpContext ctx) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            IsAuthenticated = true,
            UserId = ctx.User.FindFirst("sub")?.Value,
            Email = ctx.User.FindFirst("email")?.Value,
            Name = ctx.User.FindFirst("name")?.Value
                      ?? ctx.User.FindFirst("given_name")?.Value,
            Roles = ctx.User.Claims
                          .Where(c => c.Type == "role")
                          .Select(c => c.Value)
                          .ToArray()
        });
    }).AllowAnonymous().RequireRateLimiting("anonymous");
    // ── Token (dev only) ──────────────────────────────────────────────────────
    // The BFF keeps tokens server-side; handing the raw JWT to the browser is only meant for
    // testing Swagger / curl locally, so it is opt-in via configuration. The route always
    // exists so that a disabled endpoint explains itself instead of showing a bare 404 page.
    var exposeAccessToken = builder.Configuration.GetValue<bool>("Features:ExposeAccessToken");
    app.MapGet("/account/token", async (HttpContext ctx) =>
    {
        if (!exposeAccessToken)
            return Results.Json(new
            {
                error = "access_token_endpoint_disabled",
                message = "This endpoint is turned off. For local development set " +
                          "EXPOSE_ACCESS_TOKEN=true in your .env file (Features:ExposeAccessToken=true) " +
                          "and restart the gateway: docker-compose up -d gateway. " +
                          "Never enable it on anything reachable from the internet."
            }, statusCode: StatusCodes.Status404NotFound);

        if (ctx.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        var token = await ctx.GetTokenAsync("access_token");
        return Results.Ok(new { access_token = token });
    }).AllowAnonymous().RequireRateLimiting("anonymous");


    // Public endpoints — anonymous rate limit
    // ── Dev Portal Landing Page ───────────────────────────────────────────────
    // "/" belongs to the storefront; the portal for developers is at /portal
    app.MapGet("/portal", () => Results.Content("""
<!DOCTYPE html>
<html>
<head>
    <title>ShopEasy Dev Portal</title>
    <style>
        body { font-family: -apple-system, sans-serif; max-width: 860px;
               margin: 60px auto; padding: 0 20px; background: #f8fafc; }
        h1 { color: #1e293b; }
        .subtitle { color: #64748b; margin-bottom: 40px; }
        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
        .card { background: white; border-radius: 12px; padding: 24px;
                box-shadow: 0 1px 4px rgba(0,0,0,.08); }
        .card h3 { margin: 0 0 8px; color: #1e293b; }
        .card p { margin: 0 0 16px; color: #64748b; font-size: 14px; }
        a.btn { display: inline-block; padding: 8px 16px; background: #2563eb;
                color: white; border-radius: 8px; text-decoration: none;
                font-size: 14px; font-weight: 500; }
        a.btn:hover { background: #1d4ed8; }
        .auth { background: #f0fdf4; border: 1px solid #86efac;
                border-radius: 12px; padding: 20px; margin-bottom: 24px; }
        .auth h3 { margin: 0 0 12px; color: #166534; }
        .auth ol { margin: 0; padding-left: 20px; color: #166534;
                   font-size: 14px; line-height: 1.8; }
        .auth a { color: #15803d; }
    </style>
</head>
<body>
    <h1>ShopEasy Dev Portal</h1>
    <p class="subtitle">Development &amp; operations tools</p>

    <div class="auth">
        <h3>How to test protected endpoints</h3>
        <ol>
            <li>Login at <a href="/account/login">/account/login</a>
                (demo users are only created when <code>SEED_DEMO_USERS=true</code> in <code>.env</code>)</li>
            <li>Get your token at <a href="/account/token">/account/token</a> (local development only: needs <code>EXPOSE_ACCESS_TOKEN=true</code> in <code>.env</code>)</li>
            <li>Copy the <code>access_token</code> value</li>
            <li>Open any Swagger UI below → click <strong>Authorize</strong> → paste the token</li>
        </ol>
    </div>

    <div class="grid">
        <div class="card">
            <h3>Product Service</h3>
            <p>Browse, create, update, delete products. Anonymous read, admin write.</p>
            <a class="btn" href="http://localhost:5101">Open Swagger</a>
        </div>
        <div class="card">
            <h3>Order Service</h3>
            <p>Place and track orders. Calls Inventory synchronously, publishes events.</p>
            <a class="btn" href="http://localhost:5102">Open Swagger</a>
        </div>
        <div class="card">
            <h3>Inventory Service</h3>
            <p>Check stock levels, view transaction history, adjust quantities.</p>
            <a class="btn" href="http://localhost:5103">Open Swagger</a>
        </div>
        <div class="card">
            <h3>Payment Service</h3>
            <p>View payment records. Processes payments asynchronously via RabbitMQ.</p>
            <a class="btn" href="http://localhost:5104">Open Swagger</a>
        </div>
    </div>

    <div class="grid" style="margin-top:16px">
        <div class="card">
            <h3>IdentityServer</h3>
            <p>OpenID Connect discovery document and token endpoints.</p>
            <a class="btn" href="http://localhost:5001/.well-known/openid-configuration">
                Discovery Doc</a>
        </div>
        <div class="card">
            <h3>RabbitMQ Management</h3>
            <p>View queues, exchanges, and message rates in real time.</p>
            <a class="btn" href="http://localhost:15672">Open Dashboard</a>
        </div>
        <div class="card">
            <h3>Outbox Monitor</h3>
            <p>View pending and delivered outbox messages in real time.</p>
            <a class="btn" href="/outbox">Open Monitor</a>
        </div>
        <div class="card">
            <h3>Jaeger Tracing</h3>
            <p>Distributed traces across all services. Search by service or trace ID.</p>
            <a class="btn" href="http://localhost:16686">Open Jaeger</a>
        </div>
        <div class="card">
           <h3>Saga Monitor</h3>
           <p>Track order saga state transitions — inventory reservation, payment, compensation.</p>
           <a class="btn" href="/sagas">Open Monitor</a>
        </div> 
         <div class="card">
           <h3>Dead Letter Monitor</h3>
           <p>Track failed messages across all service queues. Requeue to retry.</p>
           <a class="btn" href="/deadletters">Open Monitor</a>
        </div>

        <div class="card">
           <h3>Grafana Dashboards</h3>
           <p>Request rates, error rates, saga durations, cache hit ratios.</p>
           <a class="btn" href="http://localhost:3000">Open Grafana</a>
       </div>
       <div class="card">
           <h3>Prometheus</h3>
           <p>Raw metrics and query explorer.</p>
           <a class="btn" href="http://localhost:9090">Open Prometheus</a>
       </div>
       <div class="card">
           <h3>Seq Log Explorer</h3>
           <p>Centralized structured logs from all services. Search by correlation ID, service, or level.</p>
           <a class="btn" href="http://localhost:5341">Open Seq</a>
       </div>
       <div class="card">
          <h3>pgAdmin</h3>
          <p>PostgreSQL database explorer for all services.</p>
          <a class="btn" href="http://localhost:5050">Open pgAdmin</a>
       </div>
    </div>
</body>
</html>
""", "text/html")).AllowAnonymous().RequireRateLimiting("anonymous");
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "gateway" })).AllowAnonymous().RequireRateLimiting("anonymous");
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = WriteHealthResponse
    }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse
    }).AllowAnonymous();

    // Dev tools — authenticated limit
    app.MapGet("/outbox", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Outbox Monitor — ShopEasy</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
       background:#f8fafc;color:#1e293b;padding:40px 32px;min-height:100vh}
  .top{display:flex;align-items:flex-start;justify-content:space-between;margin-bottom:32px}
  h1{font-size:20px;font-weight:600;color:#0f172a}
  .sub{font-size:13px;color:#64748b;margin-top:3px;font-family:monospace}
  .actions{display:flex;gap:8px;align-items:center}
  .badge{font-size:12px;font-weight:500;padding:3px 10px;border-radius:20px}
  .badge.ok{background:#dcfce7;color:#15803d}
  .badge.warn{background:#fef9c3;color:#a16207}
  .btn{font-size:13px;padding:6px 14px;border-radius:8px;border:1px solid #e2e8f0;
       background:white;cursor:pointer;color:#334155;text-decoration:none;display:inline-block}
  .btn:hover{background:#f1f5f9}
  .stats{display:grid;grid-template-columns:repeat(2,1fr);gap:12px;margin-bottom:28px}
  .stat{background:white;border:1px solid #e2e8f0;border-radius:10px;padding:16px 20px}
  .stat-label{font-size:12px;color:#64748b;margin-bottom:6px}
  .stat-val{font-size:24px;font-weight:600;color:#0f172a}
  .stat-val.ok{color:#16a34a}
  .stat-val.warn{color:#d97706}
  table{width:100%;border-collapse:collapse;background:white;
        border:1px solid #e2e8f0;border-radius:10px;overflow:hidden;font-size:13px}
  th{text-align:left;padding:10px 16px;font-size:11px;font-weight:600;
     color:#64748b;background:#f8fafc;border-bottom:1px solid #e2e8f0;
     letter-spacing:.4px;text-transform:uppercase}
  td{padding:11px 16px;border-bottom:1px solid #f1f5f9;vertical-align:top}
  tr:last-child td{border-bottom:none}
  tr:hover td{background:#f8fafc}
  .mono{font-family:monospace;font-size:11px;color:#64748b}
  .type{font-family:monospace;font-size:12px;color:#4f46e5;font-weight:500}
  .preview{font-family:monospace;font-size:11px;color:#94a3b8;
           max-width:240px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .dot{display:inline-block;width:7px;height:7px;border-radius:50%;
       margin-right:6px;vertical-align:middle}
  .dot.pending{background:#f59e0b}
  .dot.ok{background:#22c55e}
  .empty{text-align:center;padding:48px 16px;color:#94a3b8;font-size:13px}
  .warn-box{padding:14px 16px;background:#fefce8;border:1px solid #fde68a;
            border-radius:8px;font-size:13px;color:#92400e}
  .warn-box a{color:#4f46e5}
  .retry{font-size:11px;color:#b45309;margin-left:4px}
</style>
</head>
<body>
<div class="top">
  <div>
    <h1>Outbox monitor</h1>
    <div class="sub">Custom transactional outbox — OrderService</div>
  </div>
  <div class="actions">
    <span class="badge ok" id="badge">Healthy</span>
    <button class="btn" onclick="load()">↻ Refresh</button>
    <a href="/portal" class="btn">← Back</a>
  </div>
</div>
<div class="stats">
  <div class="stat">
    <div class="stat-label">Pending delivery</div>
    <div class="stat-val ok" id="stat-count">—</div>
  </div>
  <div class="stat">
    <div class="stat-label">Auto-refresh</div>
    <div class="stat-val" style="font-size:14px;color:#64748b;padding-top:6px">every 5 seconds</div>
  </div>
</div>
<div id="content"><div class="empty">Loading…</div></div>
<script>
function fmt(dt){
  if(!dt) return '—';
  return new Date(dt).toLocaleTimeString('en-GB',{hour:'2-digit',minute:'2-digit',second:'2-digit'});
}
function shortId(id){ return id ? id.toString().substring(0,8)+'…' : '—'; }
async function load(){
  const el=document.getElementById('content');
  const badge=document.getElementById('badge');
  const sc=document.getElementById('stat-count');
  try{
    const res=await fetch('/api/outbox/pending');
    if(res.status===401||res.status===403){
      el.innerHTML=`<div class="warn-box">Not authorized — <a href="/account/login">log in as Alice (admin)</a> then return here.</div>`;
      return;
    }
    const data=await res.json();
    const items=data.items||[];
    sc.textContent=data.count??items.length;
    if(items.length===0){
      badge.className='badge ok'; badge.textContent='Healthy';
      sc.className='stat-val ok';
      el.innerHTML=`<div class="empty"><span class="dot ok"></span>All messages delivered — outbox is empty</div>`;
      return;
    }
    badge.className='badge warn';
    badge.textContent=items.length+' pending';
    sc.className='stat-val warn';
    el.innerHTML=`<table>
      <thead><tr>
        <th style="width:90px">ID</th>
        <th style="width:170px">Event type</th>
        <th style="width:90px">Created at</th>
        <th style="width:100px">Status</th>
        <th>Payload preview</th>
      </tr></thead>
      <tbody>${items.map(m=>`<tr>
        <td><span class="mono">${shortId(m.id)}</span></td>
        <td><span class="type">${m.eventType||'—'}</span></td>
        <td><span class="mono">${fmt(m.createdAt)}</span></td>
        <td><span class="dot pending"></span><span style="font-size:12px;color:#d97706">Pending</span>
          ${m.retryCount>0?`<span class="retry">(${m.retryCount} retries)</span>`:''}</td>
        <td><span class="preview">${(m.payload||'').replace(/</g,'&lt;')}</span></td>
      </tr>`).join('')}</tbody></table>`;
  }catch(e){
    el.innerHTML=`<div class="warn-box">Failed to load: ${e.message}</div>`;
  }
}
load();
setInterval(load,5000);
</script>
</body>
</html>
""", "text/html")).RequireAuthorization().RequireRateLimiting("authenticated");
    app.MapGet("/sagas", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Saga Monitor — ShopEasy</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
       background:#f8fafc;color:#1e293b;padding:32px;min-height:100vh}
  .top{display:flex;align-items:flex-start;justify-content:space-between;margin-bottom:24px}
  h1{font-size:18px;font-weight:500;color:#0f172a}
  .sub{font-size:13px;color:#64748b;margin-top:3px;font-family:monospace}
  .actions{display:flex;gap:8px;align-items:center}
  .badge{font-size:12px;font-weight:500;padding:3px 10px;border-radius:20px}
  .badge-ok{background:#dcfce7;color:#15803d}
  .badge-warn{background:#fef9c3;color:#a16207}
  .badge-err{background:#fee2e2;color:#b91c1c}
  .btn{font-size:13px;padding:6px 12px;border-radius:8px;border:0.5px solid #e2e8f0;
       background:white;cursor:pointer;color:#334155;text-decoration:none;display:inline-block}
  .btn:hover{background:#f1f5f9}
  .stats{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin-bottom:24px}
  .stat{background:#f1f5f9;border-radius:8px;padding:14px 16px}
  .stat-label{font-size:11px;color:#64748b;margin-bottom:4px;text-transform:uppercase;letter-spacing:.4px}
  .stat-val{font-size:22px;font-weight:500;color:#0f172a}
  .stat-val.green{color:#16a34a}
  .stat-val.amber{color:#d97706}
  .stat-val.red{color:#dc2626}
  table{width:100%;border-collapse:collapse;background:white;
        border:0.5px solid #e2e8f0;border-radius:10px;overflow:hidden;font-size:13px}
  th{text-align:left;padding:10px 14px;font-size:11px;font-weight:500;color:#64748b;
     background:#f8fafc;border-bottom:0.5px solid #e2e8f0;
     text-transform:uppercase;letter-spacing:.4px}
  td{padding:11px 14px;border-bottom:0.5px solid #f1f5f9;vertical-align:middle}
  tr:last-child td{border-bottom:none}
  tr:hover td{background:#f8fafc}
  .mono{font-family:monospace;font-size:11px;color:#64748b}
  .order-id{font-family:monospace;font-size:12px;color:#4f46e5}
  .status-pill{display:inline-flex;align-items:center;gap:5px;padding:3px 10px;
               border-radius:20px;font-size:12px;font-weight:500}
  .s-Completed{background:#dcfce7;color:#15803d}
  .s-Cancelled{background:#fee2e2;color:#b91c1c}
  .s-Failed{background:#fee2e2;color:#b91c1c}
  .s-ProcessingPayment{background:#fef9c3;color:#a16207}
  .s-ReservingInventory{background:#dbeafe;color:#1d4ed8}
  .s-ReleasingInventory{background:#ede9fe;color:#6d28d9}
  .s-Completing{background:#dcfce7;color:#15803d}
  .s-Started{background:#f1f5f9;color:#475569}
  .dot{width:6px;height:6px;border-radius:50%;display:inline-block;flex-shrink:0}
  .dot-green{background:#22c55e}
  .dot-red{background:#ef4444}
  .dot-amber{background:#f59e0b;animation:pulse 1.5s infinite}
  .dot-blue{background:#3b82f6;animation:pulse 1.5s infinite}
  .dot-purple{background:#8b5cf6;animation:pulse 1.5s infinite}
  .dot-gray{background:#94a3b8}
  .duration{font-size:12px;color:#64748b;font-family:monospace}
  .empty{text-align:center;padding:40px;color:#94a3b8;font-size:13px}
  .warn-box{padding:12px 14px;background:#fefce8;border:0.5px solid #fde68a;
            border-radius:8px;font-size:13px;color:#92400e;margin-bottom:16px}
  .warn-box a{color:#4f46e5}
  .timeline{display:flex;align-items:center;gap:3px;flex-wrap:wrap}
  .tl-step{font-size:10px;padding:2px 6px;border-radius:4px;font-family:monospace;white-space:nowrap}
  .tl-done{background:#dcfce7;color:#15803d}
  .tl-active{background:#dbeafe;color:#1d4ed8}
  .tl-pending{background:#f1f5f9;color:#94a3b8}
  .tl-fail{background:#fee2e2;color:#b91c1c}
  .tl-arrow{color:#cbd5e1;font-size:10px}
  .order-link{cursor:pointer;text-decoration:underline dotted}
  .detail{margin-top:20px;background:white;border:0.5px solid #e2e8f0;border-radius:10px;padding:16px 18px}
  .detail-head{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:12px}
  .detail h2{font-size:14px;font-weight:500;color:#0f172a}
  .detail-sub{font-size:12px;color:#64748b;font-family:monospace;margin-top:3px}
  .detail table{border:none;border-radius:0}
  @keyframes pulse{0%,100%{opacity:1}50%{opacity:.4}}
</style>
</head>
<body>

<div class="top">
  <div>
    <h1>Saga monitor</h1>
    <div class="sub">Order saga orchestrator — state transitions</div>
  </div>
  <div class="actions">
    <span class="badge badge-ok" id="badge">Loading…</span>
    <button class="btn" onclick="load()">↻ Refresh</button>
    <a href="/portal" class="btn">← Back</a>
  </div>
</div>

<div class="stats">
  <div class="stat"><div class="stat-label">Total</div>
    <div class="stat-val" id="s-total">—</div></div>
  <div class="stat"><div class="stat-label">Completed</div>
    <div class="stat-val green" id="s-completed">—</div></div>
  <div class="stat"><div class="stat-label">In progress</div>
    <div class="stat-val amber" id="s-progress">—</div></div>
  <div class="stat"><div class="stat-label">Cancelled / Failed</div>
    <div class="stat-val red" id="s-failed">—</div></div>
  <div class="stat"><div class="stat-label">Timed out</div>
    <div class="stat-val red" id="s-timedout">—</div></div>
</div>

<div id="content"><div class="empty">Loading…</div></div>
<div id="detail"></div>

<script>
const STATUS = {
  0:'Started',
  1:'ReservingInventory',
  2:'ProcessingPayment',
  3:'Completing',
  4:'Completed',
  5:'ReleasingInventory',
  6:'Cancelled',
  7:'Failed',
  8:'RefundingPayment',
  9:'RestockingInventory'
};

const PAYMENT_STATUS = {
  0: { label:'Pending',   color:'#a16207', bg:'#fef9c3' },
  1: { label:'Succeeded', color:'#15803d', bg:'#dcfce7' },
  2: { label:'Failed',    color:'#b91c1c', bg:'#fee2e2' },
  3: { label:'Refunded',  color:'#6d28d9', bg:'#ede9fe' }
};

function statusName(s){
  return STATUS[s] ?? String(s);
}

function dotClass(name){
  if(name==='Completed') return 'dot-green';
  if(name==='Cancelled'||name==='Failed') return 'dot-red';
  if(name==='ProcessingPayment') return 'dot-amber';
  if(name==='ReservingInventory') return 'dot-blue';
  if(name==='ReleasingInventory') return 'dot-purple';
  return 'dot-gray';
}

function buildTimeline(name){
  const failed = name==='Cancelled'||name==='Failed';
  const allSteps = failed
    ? ['Started','Reserving','Payment','Releasing','Cancelled']
    : ['Started','Reserving','Payment','Completed'];

  const statusMap = {
    'Started':              ['Started'],
    'ReservingInventory':   ['Started','Reserving'],
    'ProcessingPayment':    ['Started','Reserving','Payment'],
    'Completing':           ['Started','Reserving','Payment','Completed'],
    'Completed':            ['Started','Reserving','Payment','Completed'],
    'ReleasingInventory':   ['Started','Reserving','Payment','Releasing'],
    'Cancelled':            ['Started','Reserving','Payment','Releasing','Cancelled'],
    'Failed':               ['Started','Reserving','Payment','Releasing','Cancelled'],
  };

  const done = statusMap[name] || [];

  return allSteps.map((step, i) => {
    const isDone   = done.includes(step);
    const isActive = done[done.length-1] === step &&
                     name !== 'Completed' && name !== 'Cancelled' && name !== 'Failed';
    const isFail   = (step==='Cancelled'||step==='Releasing') && failed && isDone;
    const cls      = isFail ? 'tl-fail' : isActive ? 'tl-active' : isDone ? 'tl-done' : 'tl-pending';
    const arrow    = i < allSteps.length-1 ? '<span class="tl-arrow">→</span>' : '';
    return `<span class="tl-step ${cls}">${step}</span>${arrow}`;
  }).join('');
}

function fmtDuration(ms){
  if(ms < 1000) return ms+'ms';
  if(ms < 60000) return (ms/1000).toFixed(1)+'s';
  return Math.floor(ms/60000)+'m '+(Math.floor(ms/1000)%60)+'s';
}

function fmtAge(seconds){
  if(!seconds) return '—';
  if(seconds < 60)   return seconds+'s';
  if(seconds < 3600) return Math.floor(seconds/60)+'m '+(seconds%60)+'s';
  return Math.floor(seconds/3600)+'h '+Math.floor((seconds%3600)/60)+'m';
}

function shortId(id){
  return id ? id.toString().substring(0,8)+'…' : '—';
}

function esc(text){
  return String(text ?? '').replace(/[&<>"']/g, c =>
    ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

// The history of one order: every status its saga has been in (SagaTransitions)
let openOrder = null;

function showTimeline(orderId){
  openOrder = orderId;
  renderTimeline();
}

function closeTimeline(){
  openOrder = null;
  document.getElementById('detail').innerHTML = '';
}

async function renderTimeline(){
  const el = document.getElementById('detail');
  if(!openOrder){ el.innerHTML=''; return; }
  try{
    const res = await fetch(`/api/outbox/sagas/${encodeURIComponent(openOrder)}/timeline`);
    if(!res.ok){
      el.innerHTML = `<div class="warn-box" style="margin-top:20px">No timeline for order ${esc(openOrder)} (${res.status})
        <button class="btn" onclick="closeTimeline()">Close</button></div>`;
      return;
    }
    const t = await res.json();
    const rows = (t.transitions||[]).map(x => {
      const from = x.from ? `<span class="status-pill s-${esc(x.from)}">${esc(x.from)}</span>` : '<span class="mono">start</span>';
      const after = x.secondsInPreviousStatus == null ? '—' : fmtDuration(Math.round(x.secondsInPreviousStatus*1000));
      return `<tr>
        <td class="mono">${esc(new Date(x.at).toISOString().substring(11,23))}</td>
        <td>${from} <span class="tl-arrow">→</span> <span class="status-pill s-${esc(x.to)}">${esc(x.to)}</span></td>
        <td><span class="duration">${esc(after)}</span></td>
        <td><span style="font-size:12px;color:#b91c1c">${esc(x.reason||'')}</span></td>
        <td class="mono">${esc(x.correlationId||'')}</td>
      </tr>`;
    }).join('');
    const total = t.totalSeconds == null ? '' : ` · ${fmtDuration(Math.round(t.totalSeconds*1000))} in total`;
    el.innerHTML = `<div class="detail">
      <div class="detail-head">
        <div>
          <h2>Timeline of order ${esc(t.orderId)}</h2>
          <div class="detail-sub">${esc(t.status||'')}${total}</div>
        </div>
        <button class="btn" onclick="closeTimeline()">Close</button>
      </div>
      ${rows ? `<table>
        <thead><tr><th style="width:110px">Time (UTC)</th><th>Status change</th>
          <th style="width:110px">Time in previous status</th><th>Reason</th><th>Correlation id</th></tr></thead>
        <tbody>${rows}</tbody></table>`
        : '<div class="empty">No transitions recorded: this saga is older than the history</div>'}
    </div>`;
  }catch(e){
    el.innerHTML = `<div class="warn-box" style="margin-top:20px">Failed to load the timeline: ${esc(e.message)}</div>`;
  }
}

async function load(){
  const el    = document.getElementById('content');
  const badge = document.getElementById('badge');
  try{
    // Race payments fetch against a 2-second timeout
    // If PaymentService is down, show sagas immediately without waiting
    const paymentFetch   = fetch('/api/payments').catch(() => ({ ok: false }));
    const paymentTimeout = new Promise(resolve =>
      setTimeout(() => resolve({ ok: false }), 2000));

    const [sagaRes, paymentRes] = await Promise.all([
      fetch('/api/outbox/sagas'),
      Promise.race([paymentFetch, paymentTimeout])
    ]);

    if(sagaRes.status===401||sagaRes.status===403){
      el.innerHTML=`<div class="warn-box">Not authorized —
        <a href="/account/login">log in as Alice (admin)</a> then return here.</div>`;
      return;
    }

    const sagaData         = await sagaRes.json();
    const paymentsAvailable = paymentRes.ok;
    const paymentData      = paymentsAvailable ? await paymentRes.json() : [];

    const items    = Array.isArray(sagaData) ? sagaData : (sagaData.items || []);
    const payments = Array.isArray(paymentData) ? paymentData : [];

    // Build payment lookup by orderId
    const paymentMap = {};
    payments.forEach(p => { paymentMap[p.orderId] = p; });

    const completed = items.filter(s => s.status === 4).length;
    const failed    = items.filter(s => s.status === 6 || s.status === 7).length;
    const inprog    = items.filter(s => s.status !== 4 && s.status !== 6 && s.status !== 7).length;
    const timedOut  = items.filter(s => s.isTimedOut).length;

    document.getElementById('s-total').textContent     = items.length;
    document.getElementById('s-completed').textContent = completed;
    document.getElementById('s-progress').textContent  = inprog;
    document.getElementById('s-failed').textContent    = failed;
    document.getElementById('s-timedout').textContent  = timedOut;

    if(failed > 0){
      badge.className='badge badge-err';
      badge.textContent=failed+' failed';
    } else if(inprog > 0){
      badge.className='badge badge-warn';
      badge.textContent=inprog+' in progress';
    } else {
      badge.className='badge badge-ok';
      badge.textContent='All completed';
    }

    if(items.length===0){
      el.innerHTML=`<div class="empty">No sagas yet — place an order to start one</div>`;
      return;
    }

    // Show warning banner if payment service is unavailable
    const paymentWarning = paymentsAvailable ? '' :
      `<div class="warn-box" style="margin-bottom:16px">
         ⚠ Payment service unavailable — payment status not shown
       </div>`;

    el.innerHTML = paymentWarning + `<table>
      <thead><tr>
        <th style="width:100px">Order ID</th>
        <th style="width:140px">Saga status</th>
        <th style="width:110px">Payment</th>
        <th>State journey</th>
        <th style="width:75px">Duration</th>
        <th style="width:55px">Age</th>
        <th>Failure reason</th>
      </tr></thead>
      <tbody>${items.map(s => {
        const name     = statusName(s.status);
        const payment  = paymentMap[s.orderId];
        const ps       = payment != null ? PAYMENT_STATUS[payment.status] : null;
        const timedOut = s.isTimedOut
          ? `<span style="background:#fee2e2;color:#b91c1c;font-size:11px;
                          padding:2px 5px;border-radius:4px;margin-left:4px">⚠ TIMED OUT</span>`
          : '';
        const paymentBadge = ps
          ? `<span style="background:${ps.bg};color:${ps.color};font-size:11px;
                          font-weight:500;padding:3px 8px;border-radius:20px;white-space:nowrap">
               ${ps.label}${payment.status===3?' 💜':''}
             </span>`
          : paymentsAvailable
            ? '<span style="color:#94a3b8;font-size:12px">—</span>'
            : '<span style="color:#cbd5e1;font-size:11px">unavailable</span>';

        return `<tr style="${s.isTimedOut?'background:#fef2f2':''}">
          <td><span class="order-id order-link" title="${s.orderId} — click for its timeline" onclick="showTimeline('${s.orderId}')">${shortId(s.orderId)}</span></td>
          <td>
            <span class="status-pill s-${name}">
              <span class="dot ${dotClass(name)}"></span>${name}
            </span>
            ${timedOut}
          </td>
          <td>${paymentBadge}</td>
          <td><div class="timeline">${buildTimeline(name)}</div></td>
          <td><span class="duration">${fmtDuration(s.durationMs)}</span></td>
          <td><span class="mono" style="font-size:11px;color:#94a3b8">${fmtAge(s.ageSeconds)}</span></td>
          <td><span style="font-size:12px;color:#b91c1c">${s.failureReason||'—'}</span></td>
        </tr>`;
      }).join('')}</tbody>
    </table>`;
  }catch(e){
    el.innerHTML=`<div class="warn-box">Failed to load: ${e.message}</div>`;
  }
}

load();
setInterval(() => { load(); renderTimeline(); }, 3000);
</script>
</body>
</html>
""", "text/html")).RequireAuthorization().RequireRateLimiting("authenticated");

    app.MapGet("/deadletters", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Dead Letter Monitor — ShopEasy</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
       background:#f8fafc;color:#1e293b;padding:32px;min-height:100vh}
  .top{display:flex;align-items:flex-start;justify-content:space-between;margin-bottom:24px}
  h1{font-size:18px;font-weight:500;color:#0f172a}
  .sub{font-size:13px;color:#64748b;margin-top:3px;font-family:monospace}
  .actions{display:flex;gap:8px}
  .badge{font-size:12px;font-weight:500;padding:3px 10px;border-radius:20px}
  .badge-ok{background:#dcfce7;color:#15803d}
  .badge-err{background:#fee2e2;color:#b91c1c}
  .btn{font-size:13px;padding:6px 12px;border-radius:8px;border:0.5px solid #e2e8f0;
       background:white;cursor:pointer;color:#334155;text-decoration:none;display:inline-block}
  .btn:hover{background:#f1f5f9}
  .btn-red{background:#fee2e2;color:#b91c1c;border-color:#fecaca}
  .btn-red:hover{background:#fecaca}
  .stats{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin-bottom:24px}
  .stat{background:#f1f5f9;border-radius:8px;padding:14px 16px}
  .stat-label{font-size:11px;color:#64748b;margin-bottom:4px;text-transform:uppercase;letter-spacing:.4px}
  .stat-val{font-size:22px;font-weight:500;color:#0f172a}
  .stat-val.red{color:#dc2626}
  .stat-val.green{color:#16a34a}
  table{width:100%;border-collapse:collapse;background:white;
        border:0.5px solid #e2e8f0;border-radius:10px;overflow:hidden;font-size:13px}
  th{text-align:left;padding:10px 14px;font-size:11px;font-weight:500;color:#64748b;
     background:#f8fafc;border-bottom:0.5px solid #e2e8f0;text-transform:uppercase;letter-spacing:.4px}
  td{padding:11px 14px;border-bottom:0.5px solid #f1f5f9;vertical-align:middle}
  tr:last-child td{border-bottom:none}
  tr:hover td{background:#f8fafc}
  .mono{font-family:monospace;font-size:12px;color:#4f46e5}
  .count-ok{color:#16a34a;font-weight:500}
  .count-err{color:#dc2626;font-weight:600}
  .empty{text-align:center;padding:40px;color:#94a3b8;font-size:13px}
  .warn-box{padding:12px 14px;background:#fefce8;border:0.5px solid #fde68a;
            border-radius:8px;font-size:13px;color:#92400e}
  .warn-box a{color:#4f46e5}
</style>
</head>
<body>
<div class="top">
  <div>
    <h1>Dead letter monitor</h1>
    <div class="sub">Failed messages across all service queues</div>
  </div>
  <div class="actions">
    <span class="badge badge-ok" id="badge">Checking…</span>
    <button class="btn" onclick="load()">↻ Refresh</button>
    <a href="/portal" class="btn">← Back</a>
  </div>
</div>

<div class="stats">
  <div class="stat">
    <div class="stat-label">Total failed messages</div>
    <div class="stat-val" id="s-total">—</div>
  </div>
  <div class="stat">
    <div class="stat-label">Queues with failures</div>
    <div class="stat-val red" id="s-failed">—</div>
  </div>
  <div class="stat">
    <div class="stat-label">Healthy queues</div>
    <div class="stat-val green" id="s-healthy">—</div>
  </div>
</div>

<div id="content"><div class="empty">Loading…</div></div>

<script>
function fmt(q){ return q.replace('_error',''); }

async function requeue(queueName){
  if(!confirm(`Requeue all messages from ${queueName} back to ${fmt(queueName)}?`)) return;
  const res = await fetch(`/api/outbox/deadletters/${queueName}/requeue`, {method:'POST', headers:{'X-Requested-With':'fetch'}});
  const data = await res.json();
  alert(data.message);
  load();
}

async function load(){
  const el    = document.getElementById('content');
  const badge = document.getElementById('badge');
  try{
    const res   = await fetch('/api/outbox/deadletters');
    if(res.status===401||res.status===403){
      el.innerHTML=`<div class="warn-box">Not authorized —
        <a href="/account/login">log in as Alice (admin)</a> then return here.</div>`;
      return;
    }
    const data   = await res.json();
    const queues = data.queues || [];
    const failed  = queues.filter(q=>q.messageCount>0).length;
    const healthy = queues.filter(q=>q.messageCount===0).length;

    document.getElementById('s-total').textContent   = data.totalFailedMessages;
    document.getElementById('s-total').className     = 'stat-val'+(data.totalFailedMessages>0?' red':' green');
    document.getElementById('s-failed').textContent  = failed;
    document.getElementById('s-healthy').textContent = healthy;

    if(data.hasFailures){
      badge.className='badge badge-err';
      badge.textContent=data.totalFailedMessages+' failed';
    } else {
      badge.className='badge badge-ok';
      badge.textContent='All clear';
    }

    if(queues.length===0){
      el.innerHTML=`<div class="empty">No error queues found</div>`;
      return;
    }

    el.innerHTML=`<table>
      <thead><tr>
        <th>Queue</th>
        <th style="width:120px">Failed messages</th>
        <th style="width:100px">Consumers</th>
        <th style="width:160px">Actions</th>
      </tr></thead>
      <tbody>${queues.map(q=>`<tr>
        <td><span class="mono">${q.queueName}</span></td>
        <td class="${q.messageCount>0?'count-err':'count-ok'}">${q.messageCount}</td>
        <td>${q.consumerCount}</td>
        <td>${q.messageCount>0
          ? `<button class="btn btn-red" onclick="requeue('${q.queueName}')">↩ Requeue</button>`
            + (q.managementUrl
                ? `<a class="btn" href="${q.managementUrl}" target="_blank" style="margin-left:4px">View</a>`
                : '')
          : '<span style="color:#94a3b8;font-size:12px">—</span>'
        }</td>
      </tr>`).join('')}</tbody>
    </table>`;
  }catch(e){
    el.innerHTML=`<div class="warn-box">Failed to load: ${e.message}</div>`;
  }
}
load();
setInterval(load, 15000);
</script>
</body>
</html>
""", "text/html")).RequireAuthorization().RequireRateLimiting("authenticated");

    #endregion

    app.MapReverseProxy();

    // Last: the storefront answers whatever no endpoint above claimed. Not rate limited, like the
    // static files before routing: it is the same few files and costs the services nothing.
    app.MapStorefront();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ── Health response writer ────────────────────────────────────────────────────
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