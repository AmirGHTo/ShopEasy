using FluentAssertions;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// Concurrent cache misses for the same product must share one call to Inventory
public class StockFetchCoordinatorTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static StockLevel Level(Guid id) => new(id, "Item " + id.ToString()[^1], 10, 2, 8, false);

    // A fetch the test releases by hand, recording which products were asked for
    private sealed class GatedFetch
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<Guid>> Calls { get; } = [];
        public Exception? Fail { get; set; }
        public HashSet<Guid> Unknown { get; } = [];

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<StockLevel>> Fetch(IReadOnlyList<Guid> ids)
        {
            lock (Calls) Calls.Add(ids);
            await _release.Task;
            if (Fail is not null) throw Fail;
            return ids.Where(id => !Unknown.Contains(id)).Select(Level).ToList();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().Should().BeTrue("the condition should have become true");
    }

    [Fact]
    public async Task ManyRequestsForTheSameProduct_ShareOneFetch()
    {
        var coordinator = new StockFetchCoordinator();
        var gate = new GatedFetch();

        var requests = Enumerable.Range(0, 25)
            .Select(_ => coordinator.FetchAsync([A], gate.Fetch)).ToList();
        await WaitUntil(() => gate.Calls.Count >= 1);
        gate.Release();
        var results = await Task.WhenAll(requests);

        gate.Calls.Should().ContainSingle("one call to Inventory, not 25");
        results.Should().OnlyContain(r => r[A]!.ProductId == A);
    }

    [Fact]
    public async Task OverlappingRequests_OnlyFetchWhatNobodyIsAlreadyFetching()
    {
        var coordinator = new StockFetchCoordinator();
        var gate = new GatedFetch();

        var first = coordinator.FetchAsync([A, B], gate.Fetch);
        await WaitUntil(() => gate.Calls.Count == 1);
        var second = coordinator.FetchAsync([B, C], gate.Fetch); // B is already on its way
        await WaitUntil(() => gate.Calls.Count == 2);
        gate.Release();
        var (one, two) = (await first, await second);

        gate.Calls[0].Should().BeEquivalentTo([A, B]);
        gate.Calls[1].Should().BeEquivalentTo([C], "B was already being fetched for the first request");
        two[B].Should().NotBeNull().And.Be(one[B]);
        two[C].Should().NotBeNull();
    }

    [Fact]
    public async Task AProductInventoryDoesNotKnow_ComesBackAsNull()
    {
        var coordinator = new StockFetchCoordinator();
        var gate = new GatedFetch { Unknown = { B } };
        gate.Release();

        var result = await coordinator.FetchAsync([A, B], gate.Fetch);

        result[A].Should().NotBeNull();
        result[B].Should().BeNull();
    }

    [Fact]
    public async Task AFailedFetch_FailsEveryRequestThatWaitedForIt_AndTheNextRequestTriesAgain()
    {
        var coordinator = new StockFetchCoordinator();
        var gate = new GatedFetch { Fail = new InventoryUnavailableException("down") };

        var requests = Enumerable.Range(0, 5).Select(_ => coordinator.FetchAsync([A], gate.Fetch)).ToList();
        await WaitUntil(() => gate.Calls.Count >= 1);
        gate.Release();
        foreach (var request in requests)
            await request.Awaiting(r => r).Should().ThrowAsync<InventoryUnavailableException>();
        gate.Calls.Should().ContainSingle();

        // nothing is left "in flight": the next request starts a new fetch
        var recovered = new GatedFetch();
        recovered.Release();
        (await coordinator.FetchAsync([A], recovered.Fetch))[A].Should().NotBeNull();
    }

    [Fact]
    public async Task ARequestThatGivesUpWaiting_DoesNotCancelTheFetchOthersNeed()
    {
        var coordinator = new StockFetchCoordinator();
        var gate = new GatedFetch();
        using var impatient = new CancellationTokenSource();

        var starter = coordinator.FetchAsync([A], gate.Fetch, impatient.Token); // starts the fetch
        await WaitUntil(() => gate.Calls.Count == 1);
        var patient = coordinator.FetchAsync([A], gate.Fetch);                   // joins it

        impatient.Cancel();
        await starter.Awaiting(s => s).Should().ThrowAsync<OperationCanceledException>();
        gate.Release();

        (await patient)[A].Should().NotBeNull("the fetch carried on for the request that was still waiting");
        gate.Calls.Should().ContainSingle();
    }
}
