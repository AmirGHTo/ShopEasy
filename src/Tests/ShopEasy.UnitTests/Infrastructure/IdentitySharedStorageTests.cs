using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Stores;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopEasy.Identity.Data;
using ShopEasy.Identity.Storage;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// What makes several Identity replicas behave like one: the signing keys, the Data Protection key
// ring and the issued grants live in PostgreSQL. Each "replica" below is a separate service provider
// (its own caches, its own IdentityServer) that only has the database in common — exactly the
// situation of two pods.
public class IdentitySharedStorageTests
{
    private static ServiceProvider Replica(
        PostgresTestDatabase database, IDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddIdentityServer().AddSharedStorage(configuration);
        return services.BuildServiceProvider();
    }

    // A throw-away key-encryption certificate, written as the PKCS#12 file the setting points to
    private static string WriteCertificate(string directory, string name, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(directory, $"{name}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static async Task MigrateAsync(PostgresTestDatabase database)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.ConnectionString).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static PersistedGrant Grant(string key, string subject = "alice", string client = "gateway",
        string type = "authorization_code", string? session = "s1", DateTime? expires = null) => new()
    {
        Key = key,
        Type = type,
        SubjectId = subject,
        SessionId = session,
        ClientId = client,
        CreationTime = DateTime.UtcNow,
        Expiration = expires ?? DateTime.UtcNow.AddMinutes(5),
        Data = $"data-of-{key}"
    };

    [RequiresPostgresFact]
    public async Task TwoReplicas_SignWithTheSameKey_AndItSurvivesARestart()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idkeys");
        await MigrateAsync(database);

        string firstKid;
        await using (var a = Replica(database))
        {
            var credentials = await a.GetRequiredService<IKeyMaterialService>().GetSigningCredentialsAsync();
            firstKid = credentials!.Kid;
        }

        // another replica, and the same one after a restart (a new provider = a new process)
        await using var b = Replica(database);
        var second = await b.GetRequiredService<IKeyMaterialService>().GetSigningCredentialsAsync();
        second!.Kid.Should().Be(firstKid, "the second replica must use the key the first one created");

        var keys = await b.GetRequiredService<IKeyMaterialService>().GetValidationKeysAsync();
        keys.Select(k => k.Key.KeyId).Should().Contain(firstKid);

        // and it really is in the table (not on a disk): nothing in a "keys" folder is needed
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.ConnectionString).Options);
        (await db.SigningKeys.CountAsync(k => k.Id == firstKid)).Should().Be(1);
    }

    [RequiresPostgresFact]
    public async Task DataProtection_KeyRing_IsSharedBetweenReplicas()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("iddp");
        await MigrateAsync(database);

        await using var a = Replica(database);
        var protectedByA = a.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("login-cookie").Protect("alice");

        // the login cookie issued by one pod must be readable by the other
        await using var b = Replica(database);
        b.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("login-cookie").Unprotect(protectedByA).Should().Be("alice");
    }

    [RequiresPostgresFact]
    public async Task Grants_StoredByOneReplica_AreVisibleToTheOther()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idgrants");
        await MigrateAsync(database);
        await using var a = Replica(database);
        await using var b = Replica(database);
        var storeA = a.GetRequiredService<IPersistedGrantStore>();
        var storeB = b.GetRequiredService<IPersistedGrantStore>();

        // the authorization code is issued by one pod and redeemed at the other
        await storeA.StoreAsync(Grant("code-1"));
        var redeemed = await storeB.GetAsync("code-1");
        redeemed.Should().NotBeNull();
        redeemed!.SubjectId.Should().Be("alice");
        redeemed.Data.Should().Be("data-of-code-1");
        redeemed.Expiration!.Value.Kind.Should().Be(DateTimeKind.Utc);

        // codes are single use: the redeeming pod removes it, the other must not still accept it
        await storeB.RemoveAsync("code-1");
        (await storeA.GetAsync("code-1")).Should().BeNull();
    }

    [RequiresPostgresFact]
    public async Task Grants_StoringTheSameKeyAgain_UpdatesIt()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idgrants2");
        await MigrateAsync(database);
        await using var provider = Replica(database);
        var store = provider.GetRequiredService<IPersistedGrantStore>();

        await store.StoreAsync(Grant("refresh-1", type: "refresh_token"));
        var updated = Grant("refresh-1", type: "refresh_token");
        updated.ConsumedTime = DateTime.UtcNow; // refresh-token rotation marks the old token consumed
        updated.Data = "rotated";
        await store.StoreAsync(updated);

        var stored = await store.GetAsync("refresh-1");
        stored!.Data.Should().Be("rotated");
        stored.ConsumedTime.Should().NotBeNull();
        (await store.GetAllAsync(new PersistedGrantFilter { SubjectId = "alice" })).Should().HaveCount(1);
    }

    [RequiresPostgresFact]
    public async Task Grants_Filters_MatchEveryPropertyThatIsSet()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idgrants3");
        await MigrateAsync(database);
        await using var provider = Replica(database);
        var store = provider.GetRequiredService<IPersistedGrantStore>();

        await store.StoreAsync(Grant("a", "alice", "gateway", "refresh_token", "s1"));
        await store.StoreAsync(Grant("b", "alice", "order-service", "refresh_token", "s2"));
        await store.StoreAsync(Grant("c", "alice", "gateway", "consent", "s1"));
        await store.StoreAsync(Grant("d", "bob", "gateway", "refresh_token", "s3"));

        static async Task<string[]> Keys(IPersistedGrantStore s, PersistedGrantFilter f) =>
            (await s.GetAllAsync(f)).Select(g => g.Key).OrderBy(k => k).ToArray();

        (await Keys(store, new() { SubjectId = "alice" })).Should().Equal("a", "b", "c");
        (await Keys(store, new() { SubjectId = "alice", ClientId = "gateway" })).Should().Equal("a", "c");
        (await Keys(store, new() { SubjectId = "alice", ClientId = "gateway", Type = "refresh_token" })).Should().Equal("a");
        (await Keys(store, new() { SubjectId = "alice", SessionId = "s2" })).Should().Equal("b");
        (await Keys(store, new() { ClientIds = new[] { "order-service", "nobody" } })).Should().Equal("b");
        (await Keys(store, new() { Types = new[] { "consent", "authorization_code" } })).Should().Equal("c");

        // sign-out removes one user's grants for one client; nobody else's
        await store.RemoveAllAsync(new PersistedGrantFilter { SubjectId = "alice", ClientId = "gateway" });
        (await Keys(store, new() { SubjectId = "alice" })).Should().Equal("b");
        (await Keys(store, new() { SubjectId = "bob" })).Should().Equal("d");
    }

    [RequiresPostgresFact]
    public async Task Cleanup_RemovesExpiredGrants_AndOnlyThose()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idclean");
        await MigrateAsync(database);
        await using var provider = Replica(database);
        var store = provider.GetRequiredService<IPersistedGrantStore>();

        await store.StoreAsync(Grant("expired", expires: DateTime.UtcNow.AddMinutes(-5)));
        await store.StoreAsync(Grant("valid", expires: DateTime.UtcNow.AddMinutes(5)));
        await store.StoreAsync(Grant("no-expiry", expires: null)); // falls back to +5 min in Grant()
        var noExpiry = Grant("never");
        noExpiry.Expiration = null; // e.g. consent can be remembered indefinitely
        await store.StoreAsync(noExpiry);

        var cleanup = new PersistedGrantCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistedGrantCleanupService>.Instance);
        (await cleanup.RemoveExpiredAsync(CancellationToken.None)).Should().Be(1);

        (await store.GetAsync("expired")).Should().BeNull();
        (await store.GetAsync("valid")).Should().NotBeNull();
        (await store.GetAsync("never")).Should().NotBeNull("a grant without an expiry never expires");
    }

    [RequiresPostgresFact]
    public async Task SigningKeys_StoringTheSameIdFromTwoReplicas_LeavesOneRow()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idkeys2");
        await MigrateAsync(database);
        await using var a = Replica(database);
        await using var b = Replica(database);

        var key = new SerializedKey
        {
            Id = "key-1", Version = 1, Created = DateTime.UtcNow, Algorithm = "RS256",
            DataProtected = true, Data = "ciphertext"
        };
        await a.GetRequiredService<ISigningKeyStore>().StoreKeyAsync(key);
        await b.GetRequiredService<ISigningKeyStore>().StoreKeyAsync(key);

        var loaded = (await a.GetRequiredService<ISigningKeyStore>().LoadKeysAsync()).ToList();
        loaded.Should().ContainSingle(k => k.Id == "key-1");
        loaded[0].DataProtected.Should().BeTrue();

        await b.GetRequiredService<ISigningKeyStore>().DeleteKeyAsync("key-1");
        (await a.GetRequiredService<ISigningKeyStore>().LoadKeysAsync()).Should().BeEmpty();
    }

    [RequiresPostgresFact]
    public async Task DataProtection_WithACertificate_EncryptsTheKeyRing_AndNeedsItToRead()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("iddpcert");
        await MigrateAsync(database);
        var directory = Directory.CreateTempSubdirectory("shopeasy-dp-").FullName;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = WriteCertificate(directory, "current", "pw"),
                ["DataProtection:CertificatePassword"] = "pw"
            };

            await using var a = Replica(database, settings);
            var payload = a.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("secret");

            await using var withCertificate = Replica(database, settings);
            withCertificate.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("p").Unprotect(payload).Should().Be("secret");

            // the ring in the table is encrypted: the database alone is not enough to read it
            await using (var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(database.ConnectionString).Options))
            {
                var xml = await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync();
                xml.Should().NotBeEmpty();
                xml.Should().OnlyContain(x => x!.Contains("EncryptedData") && !x.Contains("<masterKey"));
            }

            await using var withoutCertificate = Replica(database);
            var read = () => withoutCertificate.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("p").Unprotect(payload);
            read.Should().Throw<CryptographicException>("a replica without the certificate cannot read the ring");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [RequiresPostgresFact]
    public async Task DataProtection_WithoutACertificate_StoresTheRingInClearText()
    {
        // documents the default (and why the README tells you to set a certificate)
        await using var database = await PostgresTestDatabase.CreateAsync("iddpplain");
        await MigrateAsync(database);
        await using var a = Replica(database);
        a.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("x");

        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.ConnectionString).Options);
        (await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync())
            .Should().Contain(x => x!.Contains("<masterKey"));
    }

    [Fact]
    public void ACertificatePathThatDoesNotExist_FailsAtStartup_NotSilently()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataProtection:CertificatePath"] = "/no/such/file.pfx" }).Build();

        var register = () => new ServiceCollection().AddIdentityServer().AddSharedStorage(configuration);
        register.Should().Throw<InvalidOperationException>().WithMessage("*/no/such/file.pfx*");
    }
}
