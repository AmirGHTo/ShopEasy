using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopEasy.OrderService.Data;

namespace ShopEasy.OrderService.Idempotency;

public enum IdempotencyOutcome
{
    New,        // the key has not been used: process the request
    Replay,     // the same request was already processed: send back its stored response
    Mismatch    // the key was used for a different request
}

public record IdempotencyLookup(IdempotencyOutcome Outcome, IdempotencyRecord? Record = null)
{
    public static readonly IdempotencyLookup New = new(IdempotencyOutcome.New);
}

// Reads and writes the remembered responses. Writing only stages the record in the DbContext:
// the caller saves it together with the order, in one transaction. Two requests with the same key
// that run at the same moment both find nothing, both try to save, and the unique index lets only
// one of them through; the other one catches that (IsDuplicateKey) and answers from the winner.
public class IdempotencyStore(OrderDbContext db)
{
    public async Task<IdempotencyLookup> FindAsync(
        string userId, string key, string requestHash, CancellationToken ct = default)
    {
        var record = await db.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);

        if (record is null) return IdempotencyLookup.New;

        return record.RequestHash == requestHash
            ? new IdempotencyLookup(IdempotencyOutcome.Replay, record)
            : new IdempotencyLookup(IdempotencyOutcome.Mismatch, record);
    }

    public void Add(
        string userId, string key, string requestHash, Guid orderId, int statusCode, string responseBody) =>
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Key = key,
            RequestHash = requestHash,
            OrderId = orderId,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            CreatedAt = DateTime.UtcNow
        });

    // True when the save failed because another request saved the same key first
    public static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdempotencyRecord.KeyIndexName
        };
}
