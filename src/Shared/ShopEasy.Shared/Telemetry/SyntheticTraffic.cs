using Microsoft.Extensions.Configuration;

namespace ShopEasy.Shared.Telemetry;

// The synthetic probe (README, "A synthetic order every minute") places an order through the gateway every
// minute, waits for it to be confirmed and cancels it, to prove from outside that the whole path works. Those
// orders are real: they are priced, reserved, paid, refunded. They are not business, though, and a minute-by-
// minute order that is bought and given back would dominate the sales, outcome and payment figures of a quiet
// shop. The services therefore leave the orders of the probe's account out of those counters (and only those:
// latencies, queues, traces and logs still see the probe like any other customer).
//
// The account is told to the services by its e-mail address, Synthetic:ProbeEmail (PROBE_EMAIL in .env).
// Empty (the default) means there is no probe and nothing is left out.
public static class SyntheticTraffic
{
    public const string SettingName = "Synthetic:ProbeEmail";

    private static volatile string? _probeEmail;

    // Called once at start-up with the configuration of the service
    public static void Configure(IConfiguration configuration) => Configure(configuration[SettingName]);

    public static void Configure(string? probeEmail) =>
        _probeEmail = string.IsNullOrWhiteSpace(probeEmail) ? null : probeEmail.Trim();

    // Whether this e-mail address is the probe's account (addresses are compared without regard to case)
    public static bool IsProbe(string? userEmail) =>
        _probeEmail is { } probe && string.Equals(userEmail?.Trim(), probe, StringComparison.OrdinalIgnoreCase);
}
