using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The stock check in front of order placement: cache first, Inventory on a miss (one shared call
// per product), the stale copy when Inventory cannot answer.
public class InventoryCacheServiceTests
{
    private static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Webcam = Guid.Parse("00000000-0000-0000-0000-000000000005");

    private readonly IStockSource _source = Substitute.For<IStockSource>();
    private readonly IDistributedCache _redis = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly InventoryCacheService _service;

    public InventoryCacheServiceTests() =>
        _service = Create();

    private InventoryCacheService Create() => new(
        new CacheService(_redis, NullLogger<CacheService>.Instance),
        _source, new StockFetchCoordinator(), NullLogger<InventoryCacheService>.Instance);

    private static StockLevel Level(Guid id, int available = 8) => new(id, "Item", 10, 10 - available, available, false);

    private void InventoryAnswers(params StockLevel[] levels) =>
        _source.GetStockBatchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<StockLevel>>(
                levels.Where(l => call.Arg<IReadOnlyCollection<Guid>>().Contains(l.ProductId)).ToList()));

    private void InventoryIsDown() =>
        _source.GetStockBatchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<StockLevel>>(_ => throw new InventoryUnavailableException("down"));

    [Fact]
    public async Task OnAMiss_StockComesFromInventory_AndIsCachedForTheNextRequest()
    {
        InventoryAnswers(Level(Desk, available: 5));

        var first = await _service.GetStockBatchAsync([Desk]);
        var second = await _service.GetStockBatchAsync([Desk]);

        first.Should().ContainSingle().Which.AvailableQuantity.Should().Be(5);
        first[0].IsStale.Should().BeFalse();
        second.Should().ContainSingle().Which.AvailableQuantity.Should().Be(5);
        await _source.ReceivedWithAnyArgs(1).GetStockBatchAsync(default!, default);
    }

    [Fact]
    public async Task OnlyTheProductsThatMissed_AreAskedFor()
    {
        InventoryAnswers(Level(Desk), Level(Webcam));
        await _service.GetStockBatchAsync([Desk]); // Desk is cached now

        var both = await _service.GetStockBatchAsync([Desk, Webcam]);

        both.Select(s => s.ProductId).Should().BeEquivalentTo([Desk, Webcam]);
        await _source.Received(1).GetStockBatchAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(Webcam)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManyConcurrentRequestsForAnExpiredProduct_MakeOneCallToInventory()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _source.GetStockBatchAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return (IReadOnlyList<StockLevel>)[Level(Desk)];
            });

        var requests = Enumerable.Range(0, 30).Select(_ => _service.GetStockBatchAsync([Desk])).ToList();
        await Task.Delay(200); // every request has missed the cache and is waiting
        release.SetResult();
        var results = await Task.WhenAll(requests);

        calls.Should().Be(1, "a stampede of 30 requests must not become 30 calls");
        results.Should().OnlyContain(r => r.Count == 1 && r[0].ProductId == Desk);
    }

    [Fact]
    public async Task WhenInventoryIsUnavailable_TheStaleCopyAnswers_AndIsMarkedStale()
    {
        InventoryAnswers(Level(Desk, available: 4));
        await _service.GetStockBatchAsync([Desk]);        // fills the cache, including the stale copy
        await _redis.RemoveAsync(ShopEasy.Shared.Services.CacheKeys.Inventory(Desk)); // the 30 s entry expires
        InventoryIsDown();

        var result = await _service.GetStockBatchAsync([Desk]);

        result.Should().ContainSingle();
        result[0].IsStale.Should().BeTrue();
        result[0].AvailableQuantity.Should().Be(4);
    }

    [Fact]
    public async Task WhenInventoryIsUnavailableAndNothingIsCached_TheProductIsLeftOut_NotAnError()
    {
        InventoryIsDown();

        var result = await _service.GetStockBatchAsync([Desk]);

        result.Should().BeEmpty("the order endpoint rejects a product it has no stock data for");
    }

    [Fact]
    public async Task AProductInventoryDoesNotKnow_IsLeftOut()
    {
        InventoryAnswers(Level(Desk)); // Webcam is not there

        var result = await _service.GetStockBatchAsync([Desk, Webcam]);

        result.Select(s => s.ProductId).Should().BeEquivalentTo([Desk]);
    }

    [Fact]
    public async Task AProductListedTwice_IsLookedUpOnce()
    {
        InventoryAnswers(Level(Desk));

        var result = await _service.GetStockBatchAsync([Desk, Desk]);

        result.Should().ContainSingle();
    }
}
