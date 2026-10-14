namespace ShopEasy.Identity.Storage;

// One IdentityServer signing key, as handed over by its automatic key management
// (Duende's SerializedKey). Stored in PostgreSQL so that every Identity replica signs and
// validates with the same keys, and so that a restart or a rolling update keeps issued tokens valid.
public class SigningKeyEntity
{
    public string Id { get; set; } = default!;
    public int Version { get; set; }
    public DateTime Created { get; set; }
    public string Algorithm { get; set; } = default!;

    // True when Data is encrypted with ASP.NET Data Protection (the key ring is shared too, see
    // IdentityStorageExtensions). Without it the key would be unreadable on another replica.
    public bool DataProtected { get; set; }

    public string Data { get; set; } = default!;
}
