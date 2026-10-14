namespace ShopEasy.Identity.Storage;

// An authorization code, refresh token, reference token or consent record (Duende's PersistedGrant).
// By default IdentityServer keeps these in memory of ONE process, which breaks as soon as there
// is more than one replica: the code issued by one pod is redeemed at another.
public class PersistedGrantEntity
{
    public string Key { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string? SubjectId { get; set; }
    public string? SessionId { get; set; }
    public string ClientId { get; set; } = default!;
    public string? Description { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? Expiration { get; set; }
    public DateTime? ConsumedTime { get; set; }
    public string Data { get; set; } = default!;
}
