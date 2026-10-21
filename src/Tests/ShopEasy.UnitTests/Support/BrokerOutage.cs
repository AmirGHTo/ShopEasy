using MassTransit;
using ShopEasy.OrderService.Data;

namespace ShopEasy.UnitTests.Support;

// Stands in for RabbitMQ refusing a message: the next sends of the named command fail the way a
// lost broker connection does (a transient IOException). Attach BeforeSend to the bus with
// cfg.ConfigureSend(s => s.UseExecute(outage.BeforeSend)): the outbox delivers through the send
// pipeline, so this fails the delivery, not the consumer or service that stored the command.
public sealed class BrokerOutage(string command)
{
    private int _remaining;
    private int _injected;

    public int Injected => Volatile.Read(ref _injected);

    // What the saga held in the database at the moment a send failed (read through another connection)
    public Func<SagaStatus>? ReadSaga { get; set; }
    public SagaStatus? SagaWhenItFailed { get; private set; }

    public void FailNext(int sends) => Volatile.Write(ref _remaining, sends);

    public void BeforeSend(SendContext context)
    {
        if (context.DestinationAddress?.ToString().Contains(command) != true) return;
        if (Interlocked.Decrement(ref _remaining) < 0) return;

        Interlocked.Increment(ref _injected);
        SagaWhenItFailed ??= ReadSaga?.Invoke();
        throw new IOException("injected: the broker connection was lost");
    }
}
