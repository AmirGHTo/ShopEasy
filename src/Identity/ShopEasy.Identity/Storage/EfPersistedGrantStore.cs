using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Storage;

// Authorization codes, refresh tokens and consent records in the database, shared by every
// Identity replica. (Duende ships an Entity Framework store, but 7.4.x of it needs EF Core 9 and
// this solution stays on EF Core 8, so the small store interface is implemented here.)
public class EfPersistedGrantStore(IServiceScopeFactory scopes) : IPersistedGrantStore
{
    public async Task StoreAsync(PersistedGrant grant)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var row = await db.PersistedGrants.FindAsync(grant.Key);
            var isNew = row is null;
            row ??= new PersistedGrantEntity { Key = grant.Key };

            row.Type = grant.Type;
            row.SubjectId = grant.SubjectId;
            row.SessionId = grant.SessionId;
            row.ClientId = grant.ClientId;
            row.Description = grant.Description;
            row.CreationTime = grant.CreationTime;
            row.Expiration = grant.Expiration;
            row.ConsumedTime = grant.ConsumedTime;
            row.Data = grant.Data;
            if (isNew) db.PersistedGrants.Add(row);

            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Two replicas stored the same key at the same moment: try again as an update
            }
        }
    }

    public async Task<PersistedGrant?> GetAsync(string key)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.PersistedGrants.AsNoTracking().SingleOrDefaultAsync(g => g.Key == key);
        return row is null ? null : ToModel(row);
    }

    public async Task<IEnumerable<PersistedGrant>> GetAllAsync(PersistedGrantFilter filter)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await Filter(db.PersistedGrants.AsNoTracking(), filter).ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    public async Task RemoveAsync(string key)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.PersistedGrants.Where(g => g.Key == key).ExecuteDeleteAsync();
    }

    public async Task RemoveAllAsync(PersistedGrantFilter filter)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await Filter(db.PersistedGrants, filter).ExecuteDeleteAsync();
    }

    // Same semantics as IdentityServer's own stores: every property that is set must match, the
    // plural forms match any of the listed values. An empty filter would match every grant, and
    // IdentityServer never asks for that.
    private static IQueryable<PersistedGrantEntity> Filter(
        IQueryable<PersistedGrantEntity> query, PersistedGrantFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.ClientId))
            query = query.Where(g => g.ClientId == filter.ClientId);

        if (filter.ClientIds is { } clientIds && clientIds.Any())
        {
            var ids = clientIds.ToArray();
            query = query.Where(g => ids.Contains(g.ClientId));
        }

        if (!string.IsNullOrWhiteSpace(filter.SessionId))
            query = query.Where(g => g.SessionId == filter.SessionId);

        if (!string.IsNullOrWhiteSpace(filter.SubjectId))
            query = query.Where(g => g.SubjectId == filter.SubjectId);

        if (!string.IsNullOrWhiteSpace(filter.Type))
            query = query.Where(g => g.Type == filter.Type);

        if (filter.Types is { } types && types.Any())
        {
            var list = types.ToArray();
            query = query.Where(g => list.Contains(g.Type));
        }

        return query;
    }

    private static PersistedGrant ToModel(PersistedGrantEntity row) => new()
    {
        Key = row.Key,
        Type = row.Type,
        // IdentityServer models SubjectId as non-null; grants without a subject read back as null
        SubjectId = row.SubjectId!,
        SessionId = row.SessionId,
        ClientId = row.ClientId,
        Description = row.Description,
        CreationTime = DateTime.SpecifyKind(row.CreationTime, DateTimeKind.Utc),
        Expiration = row.Expiration is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
        ConsumedTime = row.ConsumedTime is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null,
        Data = row.Data
    };
}
