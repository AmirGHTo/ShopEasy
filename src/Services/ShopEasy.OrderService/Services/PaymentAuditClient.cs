using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopEasy.OrderService.Services;

// What Payment holds for one order: the PaymentStatus name (Pending, Succeeded, Failed, Refunded)
// and the amount. An order can have more than one row; that is a finding, not an error.
public record PaymentRow(Guid OrderId, string Status, decimal Amount);

public interface IPaymentAuditClient
{
    // The payment rows of the given orders (at most PaymentAuditClient.MaxOrders at a time).
    // An order without a payment has no row. Throws when Payment cannot give a trustworthy answer.
    Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default);
}

// Asks the Payment service what it holds for orders, with a token the Order service got for itself
// (payment.read). It does not retry: the consistency check that calls it runs again a few minutes
// later, and a hung call is cut off by the HTTP client's timeout.
public class PaymentAuditClient(HttpClient httpClient, ITokenService tokens, ILogger<PaymentAuditClient> logger)
    : IPaymentAuditClient
{
    public const int MaxOrders = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default)
    {
        if (orderIds.Count == 0)
            return [];
        if (orderIds.Count > MaxOrders)
            throw new ArgumentException($"At most {MaxOrders} orders can be asked about at a time.", nameof(orderIds));

        var token = await tokens.GetServiceTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/payments/audit?" + string.Join('&', orderIds.Select(id => $"orderIds={id}")));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Identity may have rotated its signing key, or the client lost its scope: drop the token so
            // the next call fetches a new one, and say so
            tokens.Invalidate(token);
            logger.LogError(
                "Payment refused the Order service's token ({Status}); check the order-service client in Identity",
                (int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<PaymentRow>>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Payment returned an empty body.");
    }
}
