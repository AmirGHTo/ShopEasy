using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.Shared.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// A cache key can hold user input (the product list key contains the requested category): what
// CacheService writes to the log must not be able to start a forged line.
public class CacheServiceLoggingTests
{
    // a category sent by a client: it ends the line and starts a fake one
    private const string HostileKey = "products:all:books\r\n2026-01-01 00:00:00 [ERR] forged line:1:10";

    private readonly ListLogger<CacheService> _log = new();
    private readonly IDistributedCache _redis = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private void NoEntryContainsALineBreak() =>
        _log.Entries.Should().OnlyContain(e => !e.Message.Contains('\r') && !e.Message.Contains('\n'));

    [Fact]
    public async Task SetAndHit_LogTheKey_WithoutItsLineBreaks()
    {
        var cache = new CacheService(_redis, _log);

        await cache.SetAsync(HostileKey, new List<string> { "a" }, TimeSpan.FromMinutes(1));
        (await cache.GetAsync<List<string>>(HostileKey)).Should().NotBeNull();
        await cache.InvalidateAsync(HostileKey);

        _log.Entries.Should().HaveCount(3);
        NoEntryContainsALineBreak();
        _log.Messages(LogLevel.Debug).Should().AllSatisfy(m => m.Should().Contain("books_2026-01-01"));
    }

    [Fact]
    public async Task FailedOperations_LogTheKey_WithoutItsLineBreaks()
    {
        var broken = Substitute.For<IDistributedCache>();
        broken.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<byte[]?>(_ => throw new InvalidOperationException("redis down"));
        broken.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("redis down"));
        broken.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => throw new InvalidOperationException("redis down"));
        var cache = new CacheService(broken, _log);

        (await cache.GetAsync<List<string>>(HostileKey)).Should().BeNull();
        await cache.SetAsync(HostileKey, new List<string> { "a" }, TimeSpan.FromMinutes(1));
        await cache.InvalidateAsync(HostileKey);

        _log.Messages(LogLevel.Warning).Should().HaveCount(3);
        NoEntryContainsALineBreak();
    }

    [Fact]
    public async Task AnOrdinaryKey_IsLoggedAsItIs()
    {
        var cache = new CacheService(_redis, _log);

        await cache.SetAsync("products:all:books:1:10", new List<string> { "a" }, TimeSpan.FromMinutes(1));

        _log.Messages(LogLevel.Debug).Should().ContainSingle().Which.Should().Contain("products:all:books:1:10");
    }
}
