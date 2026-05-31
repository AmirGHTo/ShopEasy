using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace ShopEasy.Shared.Services;

// Generic distributed cache service using Redis.
// Wraps IDistributedCache with typed get/set/invalidate operations.
public class CacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<CacheService> _logger;

    public CacheService(IDistributedCache cache, ILogger<CacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    // Get a cached value. Returns null if not found.
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        try
        {
            var cached = await _cache.GetStringAsync(key);
            if (cached is null) return null;

            _logger.LogDebug("Cache HIT: {Key}", key);
            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (Exception ex)
        {
            // Never let cache failure break the request
            _logger.LogWarning(ex, "Cache GET failed for key {Key}", key);
            return null;
        }
    }

    // Set a cached value with expiration.
    public async Task SetAsync<T>(string key, T value, TimeSpan expiration) where T : class
    {
        try
        {
            var serialized = JsonSerializer.Serialize(value);
            await _cache.SetStringAsync(key, serialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            });
            _logger.LogDebug("Cache SET: {Key} (expires in {Expiration})", key, expiration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET failed for key {Key}", key);
        }
    }

    // Remove a specific cached value.
    public async Task InvalidateAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(key);
            _logger.LogDebug("Cache INVALIDATED: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache INVALIDATE failed for key {Key}", key);
        }
    }

    // Remove all keys matching a pattern prefix.
    // Used to invalidate all product cache entries at once.
    public async Task InvalidateByPrefixAsync(string prefix, IConnectionMultiplexer redis)
    {
        try
        {
            var server = redis.GetServer(redis.GetEndPoints().First());
            var keys = server.Keys(pattern: $"{prefix}*").ToArray();
            var db = redis.GetDatabase();

            if (keys.Any())
            {
                await db.KeyDeleteAsync(keys);
                _logger.LogDebug(
                    "Cache INVALIDATED {Count} keys with prefix {Prefix}",
                    keys.Length, prefix);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cache prefix invalidation failed for prefix {Prefix}", prefix);
        }
    }
}