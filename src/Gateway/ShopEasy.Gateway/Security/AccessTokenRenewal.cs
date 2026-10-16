using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ShopEasy.Gateway.Security;

public enum RenewalOutcome
{
    Renewed,

    // Identity no longer accepts the refresh token (expired, revoked, user disabled): sign in again
    Rejected,

    // Identity could not be asked or did not answer: try again later, keep the session meanwhile
    Unavailable,
}

public sealed record RenewalResult(
    RenewalOutcome Outcome, string? AccessToken = null, string? RefreshToken = null, DateTimeOffset? ExpiresAt = null);

public interface IAccessTokenRefresher
{
    Task<RenewalResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

// Exchanges a refresh token for a new access token at Identity.
//
// A browser page fires several requests at once, all carrying the same session cookie and so
// the same refresh token. They share one exchange, and its answer is kept for a few seconds for
// the ones that arrive a moment later with the old cookie, instead of each asking Identity.
// After a failure to reach Identity it is left alone for a few seconds, so that every request
// does not wait out its timeout.
public sealed class AccessTokenRefresher(
    IHttpClientFactory httpClients,
    IOptionsMonitor<OpenIdConnectOptions> oidc,
    IMemoryCache cache,
    ILogger<AccessTokenRefresher> logger,
    TimeProvider? clock = null,
    TimeSpan? keepAnswer = null,
    TimeSpan? keepFailure = null) : IAccessTokenRefresher
{
    public const string HttpClientName = "token-refresh";
    public const string Scheme = "oidc";

    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _keepAnswer = keepAnswer ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _keepFailure = keepFailure ?? TimeSpan.FromSeconds(10);

    public async Task<RenewalResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        // the session's refresh token is not used as a key as it is
        var key = "token-refresh:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

        Lazy<Task<RenewalResult>> exchange;
        lock (_gate)
        {
            if (!cache.TryGetValue(key, out exchange!))
            {
                // not tied to the request that starts it: every request that waits for it needs the answer
                Lazy<Task<RenewalResult>> self = null!;
                self = exchange = new Lazy<Task<RenewalResult>>(async () =>
                {
                    var result = await ExchangeAsync(refreshToken);
                    if (result.Outcome == RenewalOutcome.Unavailable)
                        cache.Set(key, self, _keepFailure); // a failure is not kept as long as an answer
                    return result;
                });
                cache.Set(key, exchange, _keepAnswer);
            }
        }

        return await exchange.Value.WaitAsync(ct);
    }

    private async Task<RenewalResult> ExchangeAsync(string refreshToken)
    {
        try
        {
            var options = oidc.Get(Scheme);
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);

            using var response = await httpClients.CreateClient(HttpClientName).PostAsync(
                configuration.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = options.ClientId!,
                    ["client_secret"] = options.ClientSecret!,
                }));

            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);

            if (!response.IsSuccessStatusCode)
            {
                var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                if (error == "invalid_grant")
                    return new RenewalResult(RenewalOutcome.Rejected);

                // anything else (an unknown client, a broken request) is not the user's doing
                logger.LogError("Identity refused to renew an access token: {Status} {Error}", (int)response.StatusCode, error);
                return new RenewalResult(RenewalOutcome.Unavailable);
            }

            var root = json.RootElement;
            return new RenewalResult(
                RenewalOutcome.Renewed,
                root.GetProperty("access_token").GetString(),
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                _clock.GetUtcNow().AddSeconds(root.GetProperty("expires_in").GetInt32()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not renew an access token: Identity is not answering properly");
            return new RenewalResult(RenewalOutcome.Unavailable);
        }
    }
}

// Keeps the access token in the session cookie valid. The gateway holds the user's tokens
// server-side-in-a-cookie and forwards the access token to the services; it lives one hour, the
// session up to thirty days. Without renewal every call fails with 401 an hour after login.
public static class AccessTokenRenewal
{
    // renewed this long before the access token runs out, so a forwarded token is never about to expire
    internal static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(60);

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var expiresAtText = context.Properties.GetTokenValue("expires_at");
        if (!DateTimeOffset.TryParse(expiresAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
            return; // a session without tokens: nothing to keep valid

        var services = context.HttpContext.RequestServices;
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (expiresAt - now > RenewBeforeExpiry)
            return;

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        if (refreshToken is null)
        {
            if (expiresAt <= now)
                await EndSessionAsync(context);
            return;
        }

        var result = await services.GetRequiredService<IAccessTokenRefresher>()
            .RefreshAsync(refreshToken, context.HttpContext.RequestAborted);

        switch (result.Outcome)
        {
            case RenewalOutcome.Renewed:
                context.Properties.UpdateTokenValue("access_token", result.AccessToken!);
                context.Properties.UpdateTokenValue("refresh_token", result.RefreshToken ?? refreshToken);
                context.Properties.UpdateTokenValue("expires_at", result.ExpiresAt!.Value.ToString("o", CultureInfo.InvariantCulture));
                context.ShouldRenew = true; // write the new tokens into the cookie
                break;

            case RenewalOutcome.Rejected:
                await EndSessionAsync(context);
                break;

            // Unavailable: keep the session and the old token; the next request tries again
        }
    }

    private static async Task EndSessionAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
