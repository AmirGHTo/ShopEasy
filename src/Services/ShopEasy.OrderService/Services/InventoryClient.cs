using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopEasy.OrderService.Services;

public record StockInfo(
    Guid ProductId,
    string ProductName,
    int AvailableQuantity,
    bool IsLowStock,
    int Quantity = 0,
    int ReservedQty = 0,
    bool IsStale = false,
    DateTime CachedAt = default);

// Typed HttpClient for calling InventoryService
// Registered with IHttpClientFactory which manages connection pooling and lifetime
public class InventoryClient(
    HttpClient httpClient,
    ITokenService tokenService,
    ILogger<InventoryClient> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ITokenService _tokenService = tokenService;
    private readonly ILogger<InventoryClient> _logger = logger;

    // Check if a product has enough stock before placing an order
    public async Task<StockInfo?> GetStockAsync(Guid productId)
    {
        // Attach the service's own Bearer token (Client Credentials grant)
        // This is service-to-service auth — no user token involved here
        var token = await _tokenService.GetServiceTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        _logger.LogInformation("Checking stock for product {ProductId}", productId);

        try
        {
            var response = await _httpClient.GetAsync($"/api/inventory/{productId}");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Product {ProductId} not found in inventory", productId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new StockInfo(
                ProductId:         root.GetProperty("productId").GetGuid(),
                ProductName:       root.GetProperty("productName").GetString()!,
                AvailableQuantity: root.GetProperty("availableQuantity").GetInt32(),
                IsLowStock:        root.GetProperty("isLowStock").GetBoolean());
        }
        catch (HttpRequestException ex)
        {
            // The Polly resilience pipeline (configured in Program.cs) will
            // have already retried 3 times before we get here.
            _logger.LogError(ex, "Failed to reach InventoryService for product {ProductId}", productId);
            throw;
        }
    }
}
