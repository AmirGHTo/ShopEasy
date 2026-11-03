namespace ShopEasy.Probe;

// Probe__* in the environment (PROBE_* in .env, see docker-compose.yml)
public sealed class ProbeSettings
{
    // The probe's own customer account. It is created once, the first time the probe cannot sign in
    // (sign-up must be open then: REGISTRATION_ENABLED), and signed in to on every run after that.
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";

    // What it buys: one unit of this product (the first one of the catalog by default). The order is
    // cancelled at the end, so the stock is the same afterwards.
    public Guid ProductId { get; set; } = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // A run starts this often (start to start), as long as the one before has ended
    public int IntervalSeconds { get; set; } = 60;

    // How long the probe waits for the order to be confirmed, and for the cancellation to be finished
    public int StepTimeoutSeconds { get; set; } = 60;

    // How often it asks whether the order has moved on, while the shop is quick (the first 15 s of a wait).
    // Not below MinPollMilliseconds: see PollAfter.
    public int PollMilliseconds { get; set; } = 1000;
    public const int MinPollMilliseconds = 500;

    // Metrics, on a port of their own that Compose does not publish
    public int MetricsPort { get; set; } = 9091;

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, IntervalSeconds));
    public TimeSpan StepTimeout => TimeSpan.FromSeconds(Math.Max(1, StepTimeoutSeconds));
    public TimeSpan Poll => TimeSpan.FromMilliseconds(Math.Max(10, PollMilliseconds));

    // The gateway lets a signed-in user make 100 requests a minute, and the probe must never be the one to
    // use them up (a limited probe would report an outage of the shop that is its own doing). A healthy
    // shop confirms an order in seconds, so the probe asks every Poll for the first FastPolling of a wait
    // and every SlowPoll after that: a minute of waiting costs 30 requests at the default PollMilliseconds
    // and 45 at the least it may be (MinPollMilliseconds), however long the outage lasts.
    public static readonly TimeSpan FastPolling = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(3);

    public TimeSpan PollAfter(TimeSpan waited) => waited < FastPolling ? Poll : (Poll > SlowPoll ? Poll : SlowPoll);

    // What is wrong with the settings, or nothing
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Email) ? "Probe__Email (PROBE_EMAIL in .env) is not set: the probe needs a customer account of its own."
        : string.IsNullOrWhiteSpace(Password) ? "Probe__Password (PROBE_PASSWORD in .env) is not set: the probe needs a customer account of its own."
        : PollMilliseconds < MinPollMilliseconds ? $"Probe__PollMilliseconds is {PollMilliseconds}: below {MinPollMilliseconds} the probe could use up the gateway's request limit for a signed-in user (100 a minute), and report an outage that is its own doing."
        : null;
}
