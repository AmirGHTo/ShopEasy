using Microsoft.AspNetCore.Http;

namespace ShopEasy.Shared.Middleware;

// Adds a correlation ID to every request.
// If the incoming request has X-Correlation-Id header, uses that.
// Otherwise generates a new one.
// The ID flows through all service calls via HTTP headers.
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Get existing correlation ID or generate new one
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
                         ?? Guid.NewGuid().ToString("N")[..8]; // short 8-char ID

        // Add to response headers so clients can trace their request
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Add to Serilog context — appears in every log line for this request
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}