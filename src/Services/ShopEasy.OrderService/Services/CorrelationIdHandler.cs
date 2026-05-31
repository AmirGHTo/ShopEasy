using Microsoft.AspNetCore.Http;

namespace ShopEasy.OrderService.Services;

// Propagates X-Correlation-Id header to outgoing HTTP requests.
// This links logs across OrderService → InventoryService calls.
public class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var correlationId = accessor.HttpContext?
            .Request.Headers["X-Correlation-Id"].FirstOrDefault();

        if (!string.IsNullOrEmpty(correlationId))
            request.Headers.TryAddWithoutValidation(
                "X-Correlation-Id", correlationId);

        return await base.SendAsync(request, ct);
    }
}