using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopEasy.OrderService.Services;

// The authoritative view of a product, as returned by the Product service.
public record CatalogProduct(Guid Id, string Name, decimal Price, bool IsActive);

// Thrown when the Product service cannot give a trustworthy answer (down, timing out,
// circuit open, malformed response). PlaceOrder turns this into HTTP 503.
public class ProductCatalogUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IProductCatalogClient
{
    // Looks up every distinct product id. The result contains one entry per id;
    // the value is null when the Product service says the product does not exist.
    // Throws ProductCatalogUnavailableException if any lookup cannot be answered.
    Task<IReadOnlyDictionary<Guid, CatalogProduct?>> GetProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default);
}

// Typed HttpClient for the Product service. Prices and names used on an order always
// come from here — never from the client request (see OrderLineResolver).
// Resilience (timeouts, retries, circuit breaker) is configured in Program.cs.
public class ProductCatalogClient(
    HttpClient httpClient,
    ILogger<ProductCatalogClient> logger) : IProductCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<Guid, CatalogProduct?>> GetProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToList();
        var products = await Task.WhenAll(ids.Select(id => GetProductAsync(id, ct)));

        var result = new Dictionary<Guid, CatalogProduct?>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
            result[ids[i]] = products[i];
        return result;
    }

    private async Task<CatalogProduct?> GetProductAsync(Guid productId, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/api/v1/products/{productId}", ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var product = await response.Content.ReadFromJsonAsync<CatalogProduct>(JsonOptions, ct);
            return product ?? throw new InvalidOperationException("Product service returned an empty body.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (client disconnected) — not a Product service failure
        }
        catch (Exception ex)
        {
            // Covers HttpRequestException, timeouts and open circuit breakers raised by the
            // resilience pipeline, and unreadable responses. All mean "we don't know the price".
            logger.LogError(ex, "Product lookup failed for {ProductId}", productId);
            throw new ProductCatalogUnavailableException(
                $"Could not retrieve product {productId} from the Product service.", ex);
        }
    }
}
