using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShopEasy.Shared.Resilience;
using ShopEasy.Shared.Services;
using ShopEasy.UnitTests.Support;
using StackExchange.Redis;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// Redis is a cache: a slow or missing Redis must cost a request a fraction of a second. The
// settings are written down in appsettings.json, the Compose file with .env.example, and the
// Kubernetes ConfigMap; the code's defaults are the truth and these tests fail when a copy differs.
public class RedisConfigurationTests
{
    private static readonly RedisOptions Defaults = new();

    public static TheoryData<string> Services => new()
    {
        "src/Services/ShopEasy.ProductService",
        "src/Services/ShopEasy.InventoryService",
        "src/Services/ShopEasy.OrderService"
    };

    [Theory]
    [MemberData(nameof(Services))]
    public void TheServicesAppsettings_ListTheRedisDefaults(string directory)
    {
        using var document = JsonDocument.Parse(RepositoryFiles.Read(directory, "appsettings.json"));
        var redis = document.RootElement.GetProperty("Redis");

        redis.GetProperty("Connection").GetString().Should().Be(Defaults.Connection);
        redis.GetProperty("ConnectTimeoutMs").GetInt32().Should().Be(Defaults.ConnectTimeoutMs);
        redis.GetProperty("CommandTimeoutMs").GetInt32().Should().Be(Defaults.CommandTimeoutMs);
    }

    [Fact]
    public void Compose_PassesTheSettingsToTheThreeServicesThatUseRedis_WithTheSameDefaults()
    {
        var compose = RepositoryFiles.Read("docker-compose.yml");

        Regex.Match(compose, @"Redis__ConnectTimeoutMs:\s*""\$\{REDIS_CONNECT_TIMEOUT_MS:-(\d+)\}""").Groups[1].Value
            .Should().Be(Defaults.ConnectTimeoutMs.ToString());
        Regex.Match(compose, @"Redis__CommandTimeoutMs:\s*""\$\{REDIS_COMMAND_TIMEOUT_MS:-(\d+)\}""").Groups[1].Value
            .Should().Be(Defaults.CommandTimeoutMs.ToString());

        foreach (var service in new[] { "product", "inventory", "order" })
            CallResilienceConfigurationTests.ServiceBlock(compose, service).Should().Contain("*redis", $"{service} uses Redis");
        Regex.Matches(compose, @"\*redis\b").Count.Should().Be(3, "only Product, Inventory and Order use Redis");
    }

    [Fact]
    public void TheKubernetesConfigMap_HasTheSameValues()
    {
        var configMap = RepositoryFiles.Read("k8s", "configmaps", "configmap.yaml");

        Regex.Match(configMap, @"Redis__ConnectTimeoutMs:\s*""(\d+)""").Groups[1].Value.Should().Be(Defaults.ConnectTimeoutMs.ToString());
        Regex.Match(configMap, @"Redis__CommandTimeoutMs:\s*""(\d+)""").Groups[1].Value.Should().Be(Defaults.CommandTimeoutMs.ToString());
    }

    [Fact]
    public void TheEnvExample_ShowsBothVariablesWithTheirDefaults()
    {
        var example = RepositoryFiles.Read(".env.example");

        example.Should().Contain($"# REDIS_CONNECT_TIMEOUT_MS={Defaults.ConnectTimeoutMs}\n");
        example.Should().Contain($"# REDIS_COMMAND_TIMEOUT_MS={Defaults.CommandTimeoutMs}\n");
    }

    [Fact]
    public void Configure_AppliesTheSettings_AndFailsFastWithoutRedis()
    {
        var options = RedisConnection.Configure(new RedisOptions { Connection = "cache:6380", ConnectTimeoutMs = 700, CommandTimeoutMs = 300 });

        var endpoint = options.EndPoints.Single().Should().BeOfType<DnsEndPoint>().Subject;
        (endpoint.Host, endpoint.Port).Should().Be(("cache", 6380));
        options.ConnectTimeout.Should().Be(700);
        options.SyncTimeout.Should().Be(300);
        options.AsyncTimeout.Should().Be(300);
        options.AbortOnConnectFail.Should().BeFalse("the service starts without Redis");
        options.BacklogPolicy.QueueWhileDisconnected.Should().BeFalse("a command to an unreachable Redis fails now instead of waiting in a queue");
    }

    [Theory]
    [InlineData("", 1000, 500, "Redis:Connection")]
    [InlineData("redis:6379", 99, 500, "Redis:ConnectTimeoutMs")]
    [InlineData("redis:6379", 1000, 0, "Redis:CommandTimeoutMs")]
    public void WrongSettings_AreRefused_WithTheNameOfTheSetting(string connection, int connectMs, int commandMs, string setting)
    {
        using var provider = ServicesFor(new Dictionary<string, string?>
        {
            ["Redis:Connection"] = connection,
            ["Redis:ConnectTimeoutMs"] = connectMs.ToString(),
            ["Redis:CommandTimeoutMs"] = commandMs.ToString()
        });

        provider.Invoking(p => p.GetRequiredService<IOptions<RedisOptions>>().Value)
            .Should().Throw<OptionsValidationException>().WithMessage($"*{setting}*");
    }

    [Fact]
    public async Task WithoutRedis_ACacheCallIsAMissAtOnce_NotAFewSecondsLater()
    {
        // a port nothing listens on
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var provider = ServicesFor(new Dictionary<string, string?> { ["Redis:Connection"] = $"127.0.0.1:{port}" });
        using var scope = provider.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<CacheService>();

        var clock = Stopwatch.StartNew();
        (await cache.GetAsync<List<string>>("products:all")).Should().BeNull();
        await cache.SetAsync("products:all", new List<string> { "a" }, TimeSpan.FromMinutes(1));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the library default is about 5 seconds for each of these calls");

        provider.GetRequiredService<IConnectionMultiplexer>().Should().BeSameAs(provider.GetRequiredService<IConnectionMultiplexer>());
    }

    private static ServiceProvider ServicesFor(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisCache(configuration);
        services.AddScoped<CacheService>();
        return services.BuildServiceProvider();
    }
}
