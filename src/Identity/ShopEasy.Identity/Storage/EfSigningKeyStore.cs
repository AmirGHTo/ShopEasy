using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Storage;

// Keeps IdentityServer's automatic signing keys in the database instead of a folder on one pod's
// disk. A scope per call: IdentityServer holds the store for the lifetime of the process, the
// DbContext must not live that long.
public class EfSigningKeyStore(IServiceScopeFactory scopes) : ISigningKeyStore
{
    public async Task<IEnumerable<SerializedKey>> LoadKeysAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.SigningKeys.AsNoTracking().ToListAsync();
        return rows.Select(r => new SerializedKey
        {
            Id = r.Id,
            Version = r.Version,
            Created = DateTime.SpecifyKind(r.Created, DateTimeKind.Utc),
            Algorithm = r.Algorithm,
            DataProtected = r.DataProtected,
            Data = r.Data
        }).ToList();
    }

    public async Task StoreKeyAsync(SerializedKey key)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.SigningKeys.FindAsync(key.Id);
        if (existing is null)
        {
            db.SigningKeys.Add(new SigningKeyEntity
            {
                Id = key.Id,
                Version = key.Version,
                Created = key.Created,
                Algorithm = key.Algorithm,
                DataProtected = key.DataProtected,
                Data = key.Data
            });
        }
        else
        {
            existing.Version = key.Version;
            existing.Created = key.Created;
            existing.Algorithm = key.Algorithm;
            existing.DataProtected = key.DataProtected;
            existing.Data = key.Data;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Another replica stored the same key id a moment ago. Keys are immutable once
            // created, so what is in the table is what we wanted.
        }
    }

    public async Task DeleteKeyAsync(string id)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.SigningKeys.Where(k => k.Id == id).ExecuteDeleteAsync();
    }
}
