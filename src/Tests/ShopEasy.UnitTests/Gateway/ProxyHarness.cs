using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopEasy.Gateway.Resilience;
using Yarp.ReverseProxy.Configuration;

namespace ShopEasy.UnitTests.Gateway;

// The gateway's reverse proxy, started on a free local port with the same resilience wiring
// Program.cs uses (AddGatewayResilience / UseGatewayResilience), in front of one fake service.
internal sealed class ProxyHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }

    private ProxyHarness(WebApplication app)
    {
        _app = app;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public static async Task<ProxyHarness> StartAsync(string service, Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                [new RouteConfig
                {
                    RouteId = "all",
                    ClusterId = "service",
                    Match = new RouteMatch { Path = "/{**catch-all}" }
                }],
                [new ClusterConfig
                {
                    ClusterId = "service",
                    Destinations = new Dictionary<string, DestinationConfig> { ["only"] = new() { Address = service } }
                }])
            .AddGatewayResilience(builder.Configuration);

        var app = builder.Build();
        app.MapReverseProxy();
        await app.StartAsync();
        return new ProxyHarness(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// A fake service on a raw TCP socket, so the test decides exactly how a connection fails.
// The first DropFirst connections are closed without an answer (politely, or with a reset); after that
// it answers every request with 200 "ok". Counts the connections it accepted.
internal sealed class FlakyService : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _dropFirst;
    private readonly bool _reset;
    private int _accepted;

    public int Accepted => Volatile.Read(ref _accepted);
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public FlakyService(int dropFirst, bool reset = false, int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _dropFirst = dropFirst;
        _reset = reset;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var number = Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => Serve(client, number));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client, int number)
    {
        try
        {
            if (number <= _dropFirst)
            {
                if (_reset) client.LingerState = new LingerOption(true, 0); // closing now sends a reset
                client.Close();
                return;
            }

            using var _ = client;
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var read = new StringBuilder();
            while (!read.ToString().Contains("\r\n\r\n"))
            {
                var n = await stream.ReadAsync(buffer, _stop.Token);
                if (n == 0) return;
                read.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }

            var response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}

// A fake service on Kestrel that answers every request with a given status, or never answers
internal sealed class ScriptedService : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _hits;
    private int _cancelled;

    public int Hits => Volatile.Read(ref _hits);
    public int RequestsCancelled => Volatile.Read(ref _cancelled);
    public string Url => _app.Urls.First();

    private ScriptedService(WebApplication app) => _app = app;

    public static async Task<ScriptedService> StartAsync(int? status = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ScriptedService? service = null;

        app.MapFallback(async context =>
        {
            Interlocked.Increment(ref service!._hits);
            if (status is { } code)
            {
                context.Response.StatusCode = code;
                await context.Response.WriteAsync("the service says no");
                return;
            }

            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } // hangs until the caller gives up
            catch (OperationCanceledException) { Interlocked.Increment(ref service!._cancelled); }
        });

        await app.StartAsync();
        service = new ScriptedService(app);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
