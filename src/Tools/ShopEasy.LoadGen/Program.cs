using ShopEasy.EndToEndTests.Support;
using ShopEasy.LoadGen;

// The demo load generator: ops/demo/load.sh, README "Traffic and failures to try".
var (settings, problem) = LoadSettings.Parse(args, Environment.GetEnvironmentVariable);
if (settings is null)
{
    Console.Error.WriteLine(problem);
    return 2;
}

// not disposed: the exit handler may run after the end of Main
var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

// New customers every run, with a password that is nowhere else: sign-up is open in the Compose stack
var password = "Aa1-" + Guid.NewGuid().ToString("N");
var runId = Guid.NewGuid().ToString("N")[..8];
var customers = new List<IShop>();
try
{
    for (var n = 1; n <= settings.Customers; n++)
        customers.Add(new ShopperShop(await Shopper.SignUpAsync($"loadgen-{runId}-{n}@shopeasy.test", password)));
}
catch (Exception error)
{
    Console.Error.WriteLine($"Could not sign the customers up through the gateway ({Stack.Gateway}): {error.Message}");
    Console.Error.WriteLine("Sign-up is limited to 10 a client address and hour (RATE_LIMIT_REGISTRATION_ATTEMPTS); is the stack up and sign-up open?");
    return 1;
}

try
{
    var generator = new LoadGenerator(settings, customers, Console.Out, Random.Shared, poll: TimeSpan.FromSeconds(1.5), settle: TimeSpan.FromSeconds(60));
    await generator.RunAsync(stop.Token);
    return 0;
}
finally
{
    customers.ForEach(c => c.Dispose());
}
