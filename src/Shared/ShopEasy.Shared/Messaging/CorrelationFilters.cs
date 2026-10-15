using System.Collections.Concurrent;
using System.Reflection;
using MassTransit;

namespace ShopEasy.Shared.Messaging;

// MassTransit filters that carry the correlation id (see CorrelationContext) across RabbitMQ.
// Registered once per service on the bus:
//
//   services.AddScoped(typeof(CorrelationConsumeFilter<>));
//   services.AddScoped(typeof(CorrelationPublishFilter<>));
//   services.AddScoped(typeof(CorrelationSendFilter<>));
//   cfg.UseConsumeFilter(typeof(CorrelationConsumeFilter<>), ctx);
//   cfg.UsePublishFilter(typeof(CorrelationPublishFilter<>), ctx);
//   cfg.UseSendFilter(typeof(CorrelationSendFilter<>), ctx);

// Incoming message → restore the id for everything the consumer logs and publishes.
public class CorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var correlationId = context.Headers.Get<string>(CorrelationContext.HeaderName);

        // Every message of the order flow carries the order id; having it as a log property
        // lets one query find the order's whole story: OrderId = '...'
        var orderId = OrderIdOf(context.Message);

        using (CorrelationContext.Push(correlationId))
        using (orderId is null ? null : Serilog.Context.LogContext.PushProperty("OrderId", orderId))
        {
            await next.Send(context);
        }
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation");

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> OrderIdProperties = new();

    private static object? OrderIdOf(T message)
    {
        var property = OrderIdProperties.GetOrAdd(typeof(T), type =>
            type.GetProperty("OrderId", BindingFlags.Public | BindingFlags.Instance));
        var value = property?.GetValue(message);
        if (value is null || value is Guid id && id == Guid.Empty) return null;
        return value;
    }
}

// Outgoing event → stamp the current id, unless the sender already set one.
public class CorrelationPublishFilter<T> : IFilter<PublishContext<T>> where T : class
{
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        CorrelationHeader.Stamp(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation");
}

// Outgoing command → same.
public class CorrelationSendFilter<T> : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        CorrelationHeader.Stamp(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation");
}

internal static class CorrelationHeader
{
    public static void Stamp(SendContext context)
    {
        var current = CorrelationContext.Current;
        if (current is null) return;

        if (context.Headers.Get<string>(CorrelationContext.HeaderName) is null)
            context.Headers.Set(CorrelationContext.HeaderName, current);
    }
}
