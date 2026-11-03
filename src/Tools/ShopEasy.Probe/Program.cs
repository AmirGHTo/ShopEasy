using Prometheus;
using ShopEasy.Probe;

// The synthetic probe: README, "A synthetic order every minute".
var builder = Host.CreateApplicationBuilder(args);

var settings = builder.Configuration.GetSection("Probe").Get<ProbeSettings>() ?? new ProbeSettings();
if (settings.Problem() is { } problem)
{
    Console.Error.WriteLine("The probe cannot start: " + problem);
    return 1;
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(sp => new Journey(sp.GetRequiredService<ProbeSettings>(), Journey.SigningInAs(settings)));
builder.Services.AddHostedService<ProbeService>();

// Metrics on a port of their own (like the services), which Compose does not publish
builder.Services.AddMetricServer(options => options.Port = (ushort)settings.MetricsPort);

await builder.Build().RunAsync();
return 0;
