namespace ShopEasy.OrderService.Services;

public class BusReadySignal
{
    private TaskCompletionSource _ready = new();
    private DateTime _lastConnectedAt = DateTime.MinValue;
    private volatile bool _isConnected = false;

    public void MarkReady()
    {
        _isConnected = true;
        _lastConnectedAt = DateTime.UtcNow;
        if (!_ready.Task.IsCompleted)
            _ready.TrySetResult();
    }

    public void MarkNotReady()
    {
        _isConnected = false;
        if (_ready.Task.IsCompleted)
            _ready = new TaskCompletionSource();
    }

    public bool IsConnected => _isConnected;
    public DateTime LastConnectedAt => _lastConnectedAt;

    public async Task WaitAsync(CancellationToken ct) =>
        await _ready.Task.WaitAsync(ct);

    public bool IsReady => _ready.Task.IsCompleted && _isConnected;
}