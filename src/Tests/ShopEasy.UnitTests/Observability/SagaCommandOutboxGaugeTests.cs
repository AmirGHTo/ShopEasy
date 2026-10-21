using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// Saga commands are saved in the Order database before they are sent. How many are waiting, and how
// long the oldest has been, is what shows that the broker is unreachable.
[Collection(MetricsCollection.Name)]
public class SagaCommandOutboxGaugeTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    private void Command(int savedSecondsAgo) => _db.Set<OutboxMessage>().Add(new OutboxMessage
    {
        MessageId = Guid.NewGuid(), SentTime = DateTime.UtcNow.AddSeconds(-savedSecondsAgo),
        ContentType = "application/vnd.masstransit+json", MessageType = "urn:message:ShopEasy.Shared.Contracts:ReserveInventoryCommand",
        Body = "{}"
    });

    private async Task<(double Pending, double OldestAge)> CycleAsync()
    {
        await _db.SaveChangesAsync();
        await new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance)
            .CheckForTimedOutSagasPublic(_db, Substitute.For<IPublishEndpoint>(), new InMemorySagaLocking(), CancellationToken.None);
        return (await MetricValues.ReadAsync("shopeasy_saga_commands_pending"),
                await MetricValues.ReadAsync("shopeasy_saga_command_oldest_pending_age_seconds"));
    }

    [Fact]
    public async Task CommandsNotSentYet_AreCounted_AndTheOldestSetsTheAge()
    {
        Command(savedSecondsAgo: 5);
        Command(savedSecondsAgo: 90);

        var (pending, oldestAge) = await CycleAsync();

        pending.Should().Be(2);
        oldestAge.Should().BeApproximately(90, 5);
    }

    [Fact]
    public async Task WhenEveryCommandHasBeenSent_BothGaugesGoBackToZero()
    {
        Command(savedSecondsAgo: 30);
        await CycleAsync();
        _db.Set<OutboxMessage>().RemoveRange(_db.Set<OutboxMessage>());

        var (pending, oldestAge) = await CycleAsync();

        pending.Should().Be(0);
        oldestAge.Should().Be(0);
    }
}
