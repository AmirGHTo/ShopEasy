using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ShopEasy.OrderService.Data;
using Xunit;

namespace ShopEasy.UnitTests.Consumers;

public class OutboxEntryTests : IDisposable
{
    private readonly OrderDbContext _db;

    public OutboxEntryTests()
    {
        // Fresh in-memory database per test
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new OrderDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OutboxEntry_WhenCreated_HasNullDeliveredAt()
    {
        // Arrange
        var entry = new OutboxEntry
        {
            Id = Guid.NewGuid(),
            EventType = "OrderConfirmedEvent",
            Payload = "{}",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        _db.OutboxEntries.Add(entry);
        await _db.SaveChangesAsync();

        // Assert
        var saved = await _db.OutboxEntries.FindAsync(entry.Id);
        saved!.DeliveredAt.Should().BeNull();
        saved.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task PendingEntries_OnlyReturnUndelivered()
    {
        // Arrange
        _db.OutboxEntries.AddRange(
            new OutboxEntry
            {
                Id = Guid.NewGuid(),
                EventType = "OrderConfirmedEvent",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow,
                DeliveredAt = null  // pending
            },
            new OutboxEntry
            {
                Id = Guid.NewGuid(),
                EventType = "OrderConfirmedEvent",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow,
                DeliveredAt = DateTime.UtcNow  // delivered
            }
        );
        await _db.SaveChangesAsync();

        // Act
        var pending = await _db.OutboxEntries
            .Where(e => e.DeliveredAt == null)
            .ToListAsync();

        // Assert
        pending.Should().HaveCount(1);
    }
}