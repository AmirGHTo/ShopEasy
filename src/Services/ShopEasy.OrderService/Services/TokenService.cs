using System.Text.Json;
using ShopEasy.Shared.Security;

namespace ShopEasy.OrderService.Services;

// The token the Order service uses when it calls another service as itself, without a user
// (OAuth 2.0 client credentials). It is issued for Inventory only and carries the single scope
// the Order service needs there (inventory.read).
public interface ITokenService
{
    // A valid service token; fetched from Identity when there is none or it is about to expire.
    // Throws ServiceTokenUnavailableException when Identity cannot provide one.
    Task<string> GetServiceTokenAsync(CancellationToken cancellationToken = default);

    // The API refused this token (for example after Identity rotated its signing key): drop it so the
    // next call fetches a new one. Does nothing if the cached token is already a different one.
    void Invalidate(string rejectedToken);
}

public class ServiceTokenUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public class TokenService : ITokenService
{
    // A token is replaced this long before it expires, so a call never starts with one that is
    // about to run out.
    internal static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromSeconds(60);

    // After Identity failed to answer it is not asked again for this long. Callers fail at once
    // (or keep using the cached token while it is still valid) instead of each waiting out the
    // connection timeout of an Identity that is down.
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);

    // A cached token is used while it has at least this long left, even when it is due for
    // replacement and Identity does not answer.
    private static readonly TimeSpan MinimumRemainingLifetime = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TokenService> _logger;
    private readonly TimeProvider _clock;

    private sealed record CachedToken(string Value, DateTimeOffset Expiry);

    private CachedToken? _cached;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    private Exception? _lastFailure;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TokenService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TokenService> logger,
        TimeProvider? clock = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetServiceTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh(out var current))
            return current;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // another caller may have fetched one while this one waited for the lock
            if (IsFresh(out current))
                return current;

            var now = _clock.GetUtcNow();
            if (now < _retryNotBefore)
                return CachedOrThrow(now, _lastFailure);

            try
            {
                return await FetchAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _lastFailure = ex;
                _retryNotBefore = _clock.GetUtcNow() + RetryAfterFailure;
                _logger.LogError(ex, "Could not get a service token from Identity");
                return CachedOrThrow(_clock.GetUtcNow(), ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate(string rejectedToken)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && cached.Value == rejectedToken)
            Interlocked.CompareExchange(ref _cached, null, cached);
    }

    private bool IsFresh(out string token)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && _clock.GetUtcNow() < cached.Expiry - RefreshBeforeExpiry)
        {
            token = cached.Value;
            return true;
        }
        token = "";
        return false;
    }

    // While Identity is not answering, a token that is due for replacement but still valid keeps
    // the service working until it really expires.
    private string CachedOrThrow(DateTimeOffset now, Exception? failure)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && now < cached.Expiry - MinimumRemainingLifetime)
            return cached.Value;
        throw new ServiceTokenUnavailableException("Identity did not provide a service token", failure);
    }

    private async Task<string> FetchAsync(CancellationToken ct)
    {
        _logger.LogInformation("Requesting a new service token from Identity (client credentials)");

        var client = _httpClientFactory.CreateClient("idp");
        var authority = (_config["IdentityServer:Authority"] ?? "http://identity:8080").TrimEnd('/');

        using var response = await client.PostAsync($"{authority}/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _config["ServiceAuth:ClientId"] ?? "order-service",
                ["client_secret"] = _config["ServiceAuth:ClientSecret"]
                    ?? throw new InvalidOperationException("ServiceAuth:ClientSecret is not configured."),
                ["scope"] = ApiScopes.InventoryRead
            }), ct);

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The token response has no access_token.");
        var expiresIn = document.RootElement.GetProperty("expires_in").GetInt32();

        Volatile.Write(ref _cached, new CachedToken(token, _clock.GetUtcNow().AddSeconds(expiresIn)));
        _lastFailure = null;

        _logger.LogInformation("Service token obtained, valid for {ExpiresIn}s", expiresIn);
        return token;
    }
}
