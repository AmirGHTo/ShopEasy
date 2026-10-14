using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ShopEasy.InventoryService.Data;

// A consumer's database work must run in exactly one transaction, whoever opened it.
// (Same helper as in the Payment service; the two services deliberately share no code.)
//
// When the consumer endpoint uses MassTransit's transactional outbox, MassTransit has already
// opened the transaction (it also writes the inbox row and the outgoing messages into it) and
// commits it after the consumer returns. The consumer must then JOIN that transaction: opening
// a second one on the same connection throws.
// Without the outbox (unit tests, or any host that does not enable it) there is no ambient
// transaction, so the consumer opens and owns one.
//
//   await using var tx = await db.Database.BeginOrJoinAsync(ct);
//   ... SaveChangesAsync ...
//   await tx.CommitAsync(ct);   // commits only if this call opened the transaction
public static class UnitOfWorkExtensions
{
    public static async Task<UnitOfWork> BeginOrJoinAsync(
        this Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database,
        CancellationToken ct = default)
    {
        if (database.CurrentTransaction is not null)
            return new UnitOfWork(null);

        return new UnitOfWork(await database.BeginTransactionAsync(ct));
    }
}

public sealed class UnitOfWork : IAsyncDisposable
{
    private readonly IDbContextTransaction? _owned;

    internal UnitOfWork(IDbContextTransaction? owned) => _owned = owned;

    // True when this consumer opened the transaction (no outbox); false when it joined
    // the transaction MassTransit manages.
    public bool OwnsTransaction => _owned is not null;

    // No-op when the transaction belongs to MassTransit: it commits after the consumer returns.
    public Task CommitAsync(CancellationToken ct = default)
        => _owned?.CommitAsync(ct) ?? Task.CompletedTask;

    // No-op when the transaction belongs to MassTransit: it rolls back if the consumer throws.
    public Task RollbackAsync(CancellationToken ct = default)
        => _owned?.RollbackAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
        => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}
