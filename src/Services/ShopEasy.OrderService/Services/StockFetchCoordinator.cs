namespace ShopEasy.OrderService.Services;

// Makes concurrent requests that miss the cache for the same product share ONE call to Inventory.
//
// When a popular product's cache entry expires, every order request that arrives in the next few
// milliseconds would otherwise ask Inventory for it, and when Inventory is slow that pile of
// identical calls is what keeps it slow (a cache stampede). Here the first request starts the
// fetch; the others wait for its answer. One instance per process.
public sealed class StockFetchCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Task<StockLevel?>> _inFlight = new();

    // Returns the level of every requested product (null where Inventory does not know it).
    // `fetch` is only called for the products nobody is already fetching. It runs to completion
    // even if the request that started it is cancelled, because other requests may be waiting for
    // it; its own deadline bounds it. Cancelling `ct` only stops THIS caller from waiting.
    public async Task<IReadOnlyDictionary<Guid, StockLevel?>> FetchAsync(
        IReadOnlyCollection<Guid> productIds,
        Func<IReadOnlyList<Guid>, Task<IReadOnlyList<StockLevel>>> fetch,
        CancellationToken ct = default)
    {
        var owned = new List<(Guid Id, TaskCompletionSource<StockLevel?> Source)>();
        var waiting = new Dictionary<Guid, Task<StockLevel?>>();

        lock (_gate)
        {
            foreach (var id in productIds.Distinct())
            {
                if (_inFlight.TryGetValue(id, out var existing))
                {
                    waiting[id] = existing;
                }
                else
                {
                    var source = new TaskCompletionSource<StockLevel?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight[id] = source.Task;
                    owned.Add((id, source));
                    waiting[id] = source.Task;
                }
            }
        }

        if (owned.Count > 0)
            _ = RunAsync(owned, fetch);

        var result = new Dictionary<Guid, StockLevel?>();
        foreach (var (id, task) in waiting)
            result[id] = await task.WaitAsync(ct);
        return result;
    }

    private async Task RunAsync(
        List<(Guid Id, TaskCompletionSource<StockLevel?> Source)> owned,
        Func<IReadOnlyList<Guid>, Task<IReadOnlyList<StockLevel>>> fetch)
    {
        try
        {
            var levels = await fetch(owned.Select(o => o.Id).ToList());
            var byProduct = levels.ToDictionary(l => l.ProductId);
            foreach (var (id, source) in owned)
                source.TrySetResult(byProduct.GetValueOrDefault(id));
        }
        catch (Exception ex)
        {
            foreach (var (_, source) in owned)
                source.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var (id, _) in owned)
                    _inFlight.Remove(id);
            }
        }
    }
}
