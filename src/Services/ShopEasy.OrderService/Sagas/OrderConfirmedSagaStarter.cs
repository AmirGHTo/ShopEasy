using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.Shared.Contracts;
using System.Text.Json;

namespace ShopEasy.OrderService.Sagas;

// Receives OrderConfirmedEvent and creates the saga state record
// The SagaOrchestrator background service then drives it forward
public class OrderConfirmedSagaStarter(
    OrderDbContext db,
    ILogger<OrderConfirmedSagaStarter> logger)
    : IConsumer<OrderConfirmedEvent>
{
    public async Task Consume(ConsumeContext<OrderConfirmedEvent> context)
    {
        var evt = context.Message;

        // Idempotency — don't create duplicate sagas
        var existing = await db.SagaStates
            .AnyAsync(s => s.OrderId == evt.OrderId);

        if (existing)
        {
            logger.LogWarning(
                "Saga already exists for Order {OrderId} — skipping",
                evt.OrderId);
            return;
        }

        logger.LogInformation(
            "Creating saga for Order {OrderId}", evt.OrderId);

        var saga = new OrderSagaState
        {
            Id = Guid.NewGuid(),
            OrderId = evt.OrderId,
            Status = SagaStatus.Started,
            UserId = evt.UserId,
            UserEmail = evt.UserEmail,
            TotalAmount = evt.TotalAmount,
            ItemsJson = JsonSerializer.Serialize(evt.Items),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SagaStates.Add(saga);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Saga {SagaId} created for Order {OrderId} — status: Started",
            saga.Id, evt.OrderId);
    }
}