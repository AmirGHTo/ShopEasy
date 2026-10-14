using System.Security.Cryptography.X509Certificates;
using Duende.IdentityServer.Stores;
using Microsoft.AspNetCore.DataProtection;

namespace ShopEasy.Identity.Storage;

public static class IdentityStorageExtensions
{
    // The application name scopes the Data Protection key ring: replicas that use the same name
    // and the same database read each other's keys.
    public const string DataProtectionApplicationName = "ShopEasy.Identity";

    // Everything that two Identity replicas must agree on lives in PostgreSQL:
    //  - the signing keys (so a token signed by one replica validates at the other, and survives
    //    a restart or rolling update),
    //  - the Data Protection key ring (the signing keys are encrypted with it, and it also protects
    //    the login cookie and anti-forgery tokens),
    //  - authorization codes, refresh tokens and consent (the code issued by one replica is
    //    redeemed at another).
    // ApplicationDbContext must be registered by the caller.
    //
    // The key ring protects the signing keys, so it is only as safe as its own storage. Without
    // DataProtection:CertificatePath it sits in the same database in clear text (ASP.NET Core logs
    // "No XML encryptor configured"): anyone who can read the database can then read the signing
    // keys. With a certificate (PKCS#12 file) the ring is encrypted and the certificate is what
    // has to be guarded, separately from the database (a Kubernetes Secret, a key vault).
    // DataProtection:PreviousCertificatePath keeps a ring readable while the certificate rotates.
    public static IIdentityServerBuilder AddSharedStorage(
        this IIdentityServerBuilder builder, IConfiguration configuration)
    {
        var dataProtection = builder.Services
            .AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<Data.ApplicationDbContext>();

        var current = LoadCertificate(configuration, "CertificatePath");
        if (current is not null)
        {
            dataProtection.ProtectKeysWithCertificate(current);

            var previous = LoadCertificate(configuration, "PreviousCertificatePath");
            if (previous is not null)
                dataProtection.UnprotectKeysWithAnyCertificate(previous);
        }

        builder.Services.AddTransient<ISigningKeyStore, EfSigningKeyStore>();
        builder.Services.AddTransient<IPersistedGrantStore, EfPersistedGrantStore>();
        builder.Services.AddHostedService<PersistedGrantCleanupService>();

        return builder;
    }

    private static X509Certificate2? LoadCertificate(IConfiguration configuration, string setting)
    {
        var path = configuration[$"DataProtection:{setting}"];
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"DataProtection:{setting} points to '{path}', which does not exist.");

        return new X509Certificate2(path, configuration["DataProtection:CertificatePassword"]);
    }
}
