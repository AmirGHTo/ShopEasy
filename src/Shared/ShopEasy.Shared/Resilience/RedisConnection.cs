using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ShopEasy.Shared.Resilience;

// Redis is only a cache here (nothing depends on it for correctness), so a slow Redis must cost a
// request no more than a missing one: the call gives up quickly and the service reads the database.
// StackExchange.Redis waits 5 seconds per command by default, so a Redis that hangs (not one that
// refuses connections) adds those 5 seconds to every request that touches the cache.
//
// Settings live under "Redis", next to "Connection". They replace connectTimeout, syncTimeout and
// asyncTimeout if those are written into the connection string.
public class RedisOptions
{
    public const string SectionName = "Redis";

    // The endpoint, e.g. "redis:6379" (StackExchange.Redis options may follow, "redis:6379,password=...")
    public string Connection { get; set; } = "redis:6379";

    // How long connecting may take. With Redis down at start-up, the first request that needs it
    // waits this long once; the library goes on reconnecting in the background.
    public int ConnectTimeoutMs { get; set; } = 1000;

    // How long one command may take before it fails and the caller reads the database instead. The
    // library looks for expired commands about once a second, so a command fails after this long
    // plus up to a second.
    public int CommandTimeoutMs { get; set; } = 500;

    internal string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Connection))
            return $"{SectionName}:{nameof(Connection)} must be set.";
        if (ConnectTimeoutMs < 100)
            return $"{SectionName}:{nameof(ConnectTimeoutMs)} must be at least 100, not {ConnectTimeoutMs}.";
        if (CommandTimeoutMs < 100)
            return $"{SectionName}:{nameof(CommandTimeoutMs)} must be at least 100, not {CommandTimeoutMs}.";
        return null;
    }
}

public static class RedisConnection
{
    public static ConfigurationOptions Configure(RedisOptions options)
    {
        if (options.Problem() is { } problem)
            throw new InvalidOperationException(problem);

        var configuration = ConfigurationOptions.Parse(options.Connection);
        configuration.AbortOnConnectFail = false;    // start, and keep running, without Redis
        configuration.ConnectTimeout = options.ConnectTimeoutMs;
        configuration.SyncTimeout = options.CommandTimeoutMs;
        configuration.AsyncTimeout = options.CommandTimeoutMs;
        configuration.ConnectRetry = 1;              // the first connect must not take several timeouts
        configuration.BacklogPolicy = BacklogPolicy.FailFast;   // no queueing while Redis is unreachable: fail now
        return configuration;
    }

    // One connection to Redis per service, used by the distributed cache and by code that needs
    // Redis itself (IConnectionMultiplexer). Wrong settings stop the service at start-up.
    public static IServiceCollection AddRedisCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<RedisOptions>, RedisOptionsValidator>();
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(Configure(sp.GetRequiredService<IOptions<RedisOptions>>().Value)));

        services.AddStackExchangeRedisCache(options => options.InstanceName = "shopeasy:");
        services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((options, sp) =>
            options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>()));

        return services;
    }

    private sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
    {
        public ValidateOptionsResult Validate(string? name, RedisOptions options) =>
            options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}
