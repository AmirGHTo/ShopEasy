namespace ShopEasy.OrderService.Idempotency;

// Settings under "Idempotency" (appsettings.json, or Idempotency__RequireKey and so on).
public class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    // When true, POST /api/orders without an Idempotency-Key header is rejected with 400. Off by
    // default so existing clients keep working; switch it on once every client sends the header.
    public bool RequireKey { get; set; }

    // How long a key is remembered. A retry within this time gets the original response; after it
    // the same key is accepted as a new request. Clients retry within seconds to hours, so a day
    // is generous.
    public int RetentionHours { get; set; } = 24;
}
