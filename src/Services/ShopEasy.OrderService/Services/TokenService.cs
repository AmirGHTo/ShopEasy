using System.Text.Json;

namespace ShopEasy.OrderService.Services;

// This service handles the Client Credentials OAuth flow.
// OrderService uses this to get its own token when calling
// InventoryService as a machine-to-machine call (no user involved).
public interface ITokenService
{
    Task<string> GetServiceTokenAsync();
}

public class TokenService : ITokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TokenService> _logger;

    // Simple in-memory cache — reuse the token until it expires
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TokenService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config            = config;
        _logger            = logger;
    }

    public async Task<string> GetServiceTokenAsync()
    {
        // Return cached token if still valid (with 60s buffer before expiry)
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
            return _cachedToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry.AddSeconds(-60))
                return _cachedToken;

            _logger.LogInformation("Requesting new service token from IdP (Client Credentials)");

            var client   = _httpClientFactory.CreateClient("idp");
            var authority = _config["IdentityServer:Authority"] ?? "http://identity:8080";

            // POST to the token endpoint with Client Credentials grant
            var response = await client.PostAsync($"{authority}/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"]    = "client_credentials",
                    ["client_id"]     = _config["ServiceAuth:ClientId"]     ?? "order-service",
                    ["client_secret"] = _config["ServiceAuth:ClientSecret"]
                                    ?? throw new InvalidOperationException("ServiceAuth:ClientSecret is not configured."),
                    ["scope"]         = "inventory.read inventory.write payment.charge"
                }));

            response.EnsureSuccessStatusCode();

            var json    = await response.Content.ReadAsStringAsync();
            var doc     = JsonDocument.Parse(json);
            var token   = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

            _cachedToken = token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            _logger.LogInformation("Service token obtained, expires in {ExpiresIn}s", expiresIn);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
