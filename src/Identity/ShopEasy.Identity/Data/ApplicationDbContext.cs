using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Storage;

namespace ShopEasy.Identity.Data;

// Extend IdentityUser if you need custom user fields
public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
}

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    // The state that all Identity replicas share (see IdentityStorageExtensions)
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = default!;
    public DbSet<SigningKeyEntity> SigningKeys { get; set; } = default!;
    public DbSet<PersistedGrantEntity> PersistedGrants { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SigningKeyEntity>(key =>
        {
            key.HasKey(k => k.Id);
            key.Property(k => k.Id).HasMaxLength(200);
            key.Property(k => k.Algorithm).HasMaxLength(100).IsRequired();
            key.Property(k => k.Data).IsRequired();
        });

        builder.Entity<PersistedGrantEntity>(grant =>
        {
            grant.HasKey(g => g.Key);
            grant.Property(g => g.Key).HasMaxLength(200);
            grant.Property(g => g.Type).HasMaxLength(50).IsRequired();
            grant.Property(g => g.SubjectId).HasMaxLength(200);
            grant.Property(g => g.SessionId).HasMaxLength(100);
            grant.Property(g => g.ClientId).HasMaxLength(200).IsRequired();
            grant.Property(g => g.Description).HasMaxLength(200);
            grant.Property(g => g.Data).IsRequired();

            // the lookups IdentityServer does: by subject/client/type, by session, and the cleanup
            grant.HasIndex(g => new { g.SubjectId, g.ClientId, g.Type });
            grant.HasIndex(g => new { g.SubjectId, g.SessionId, g.Type });
            grant.HasIndex(g => g.Expiration);
        });
    }
}
