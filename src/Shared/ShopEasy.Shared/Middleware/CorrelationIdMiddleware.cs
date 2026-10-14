using Microsoft.AspNetCore.Http;
using ShopEasy.Shared.Messaging;

namespace ShopEasy.Shared.Middleware;

// Adds a correlation ID to every request.
// If the incoming request has X-Correlation-Id header, uses that.
// Otherwise generates a new one.
// The ID flows through all service calls via HTTP headers, and — through CorrelationContext —
// into the header of every message published while the request is handled.
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Get existing correlation ID or generate new one
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
                         ?? CorrelationContext.NewId(); // short 8-char ID

        // Add to response headers so clients can trace their request
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Current id for the rest of the request (messages published from here carry it) and
        // a Serilog property — it appears in every log line for this request
        using (CorrelationContext.Push(correlationId))
        {
            await _next(context);
        }
    }
}