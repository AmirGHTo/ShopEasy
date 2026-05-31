using MassTransit;

namespace ShopEasy.OrderService.Services;

public class BusObserver : IBusObserver
{
    private readonly BusReadySignal _signal;
    private readonly ILogger<BusObserver> _logger;

    public BusObserver(BusReadySignal signal, ILogger<BusObserver> logger)
    {
        _signal = signal;
        _logger = logger;
    }

    public void PostCreate(IBus bus)
    {
        _logger.LogInformation("Outbox: bus created");
    }

    public void CreateFaulted(Exception exception)
    {
        _logger.LogWarning("Outbox: bus creation faulted: {Error}", exception.Message);
    }

    public Task PreStart(IBus bus) => Task.CompletedTask;

    public Task PostStop(IBus bus) => Task.CompletedTask;


    // Called by MassTransit when the connection to RabbitMQ is lost
    public Task PostStart(IBus bus, Task<BusReady> busReady)
    {
        _ = WatchBusReady(busReady);
        return Task.CompletedTask;
    }

    private async Task WatchBusReady(Task<BusReady> busReady)
    {
        try
        {
            await busReady;
            _signal.MarkReady();
            _logger.LogInformation("Outbox: bus connected — consumers ready");
        }
        catch (Exception ex)
        {
            _signal.MarkNotReady();
            _logger.LogWarning("Outbox: bus failed to become ready: {Error}", ex.Message);
        }
    }

    public Task PreStop(IBus bus)
    {
        _signal.MarkNotReady();
        _logger.LogInformation("Outbox: bus stopping — pausing delivery");
        return Task.CompletedTask;
    }

    public Task StartFaulted(IBus bus, Exception exception)
    {
        _signal.MarkNotReady();
        _logger.LogWarning("Outbox: bus start faulted — pausing delivery");
        return Task.CompletedTask;
    }

    public Task StopFaulted(IBus bus, Exception exception)
    {
        _signal.MarkNotReady();
        return Task.CompletedTask;
    }
}