namespace ShopEasy.OrderService.Services;

// Settings under "ConsistencyCheck" (appsettings.json, or ConsistencyCheck__IntervalSeconds and so on).
// The check compares what the Order service believes about its settled orders with what Payment holds.
public class ConsistencyCheckOptions
{
    public const string SectionName = "ConsistencyCheck";

    public bool Enabled { get; set; } = true;

    // How often the check looks for orders that settled since its last pass. Keep it under two
    // minutes: the PollingLoopStalled alert fires when a loop completes no pass for that long.
    public int IntervalSeconds { get; set; } = 60;

    // An order is checked only this long after it last changed. A refund, a late payment or a restock
    // still under way (the saga's compensation) would otherwise look like a fault.
    public int SettleSeconds { get; set; } = 120;

    // The furthest back the check looks: after a restart, or after passes failed for a while, it
    // catches up on orders that settled this long ago and not further.
    public int LookbackMinutes { get; set; } = 30;

    // Orders examined in one pass; a pass that finds more carries on with the rest at the next one.
    public int MaxOrdersPerPass { get; set; } = 500;

    // The service refuses to start with values that make no sense (see Program.cs), instead of
    // checking nothing or raising false alarms
    public bool IntervalIsValid => IntervalSeconds is >= 10 and < 120;
    public bool SettleIsValid => SettleSeconds is >= 0 and <= 3600;
    public bool LookbackIsLongEnough => Lookback > Settle + Interval;
    public bool MaxOrdersIsValid => MaxOrdersPerPass is >= 1 and <= 5000;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
    public TimeSpan Settle => TimeSpan.FromSeconds(SettleSeconds);
    public TimeSpan Lookback => TimeSpan.FromMinutes(LookbackMinutes);
}
