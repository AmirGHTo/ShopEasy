namespace ShopEasy.LoadGen;

// What the generator was asked to do. Arguments win over the environment (LOAD_* in .env / Compose).
//   --rate N        orders a minute (default 12)
//   --duration S    seconds to run; 0 (default) runs until it is stopped
//   --mix a,b,c     the shares of confirmed, declined and cancelled orders (default 70,10,20)
public sealed record LoadSettings(int RatePerMinute, int DurationSeconds, int Confirmed, int Declined, int Cancelled)
{
    // The gateway and the Order service each let one customer place 10 orders a minute; one customer is
    // asked for fewer, so a burst of the clock never meets the limit.
    public const int PerCustomerPerMinute = 8;

    // Sign-up is limited to 10 a client address and hour (RATE_LIMIT_REGISTRATION_ATTEMPTS), and each run
    // signs up its customers anew (the password is random and kept nowhere), so the rate has a top
    public const int MaxRatePerMinute = 10 * PerCustomerPerMinute;

    public int Customers => (RatePerMinute + PerCustomerPerMinute - 1) / PerCustomerPerMinute;
    public int Total => Confirmed + Declined + Cancelled;
    public TimeSpan Interval => TimeSpan.FromSeconds(60.0 / RatePerMinute);

    public static (LoadSettings? Settings, string? Problem) Parse(string[] args, Func<string, string?> environment)
    {
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

        var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--rate" or "--duration" or "--mix")).ToList();
        if (unknown.Count > 0)
            return (null, $"Unknown option {unknown[0]}. Use --rate N, --duration SECONDS, --mix CONFIRMED,DECLINED,CANCELLED.");

        if (!int.TryParse(Option("--rate") ?? environment("LOAD_RATE_PER_MINUTE") ?? "12", out var rate) || rate < 1 || rate > MaxRatePerMinute)
            return (null, $"The rate is orders a minute, from 1 to {MaxRatePerMinute}.");
        if (!int.TryParse(Option("--duration") ?? environment("LOAD_DURATION_SECONDS") ?? "0", out var duration) || duration < 0)
            return (null, "The duration is a number of seconds, 0 for no end.");

        var mix = (Option("--mix") ?? environment("LOAD_MIX") ?? "70,10,20").Split(',', StringSplitOptions.TrimEntries);
        if (mix.Length != 3 || !mix.All(part => int.TryParse(part, out var share) && share >= 0) || mix.Sum(int.Parse) == 0)
            return (null, "The mix is three shares, confirmed,declined,cancelled, for example 70,10,20.");

        return (new LoadSettings(rate, duration, int.Parse(mix[0]), int.Parse(mix[1]), int.Parse(mix[2])), null);
    }
}

public enum Intent { Confirmed, Declined, Cancelled }

// The decisions, apart from the network
public static class LoadPlan
{
    // The payment provider refuses any single payment above this (ProcessPaymentConsumer), whatever Compose
    // forces for the random bank decline: the way to provoke a decline on purpose
    public const decimal ProviderLimit = 10_000m;
    public const int MaxQuantity = 100; // per product and order (OrderLimits.MaxQuantityPerProduct)

    // ticket is a number from 0 up to the total of the mix
    public static Intent Choose(LoadSettings settings, int ticket) =>
        ticket < settings.Confirmed ? Intent.Confirmed
        : ticket < settings.Confirmed + settings.Declined ? Intent.Declined
        : Intent.Cancelled;

    // The product to decline with, and how many of it: one that has the stock to go over the provider's limit
    // (an order for more than is on the shelf is refused for stock and never reaches the provider), the one
    // with the most room. Null when no product can.
    public static (Guid Id, int Quantity)? ChooseDecline(IEnumerable<Product> products) =>
        products
            .Select(p => (p.Id, Quantity: DeclineQuantity(p.Price), p.Available))
            .Where(c => c.Quantity is { } quantity && quantity <= c.Available)
            .OrderByDescending(c => c.Available / (double)c.Quantity!.Value)
            .Select(c => ((Guid Id, int Quantity)?)(c.Id, c.Quantity!.Value))
            .FirstOrDefault();

    // How many of a product it takes to be above the provider's limit; null when more than an order may hold
    public static int? DeclineQuantity(decimal price)
    {
        if (price <= 0) return null;
        var quantity = (int)Math.Floor(ProviderLimit / price) + 1;
        return quantity <= MaxQuantity ? quantity : null;
    }
}
