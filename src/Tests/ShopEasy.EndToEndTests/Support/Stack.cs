namespace ShopEasy.EndToEndTests.Support;

// Where the running system is. The defaults are the ports of docker-compose.yml; the variables let
// the same tests run against a stack that was started some other way.
public static class Stack
{
    public static Uri Gateway { get; } = FromEnvironment("E2E_GATEWAY_URL", "http://localhost:5000");

    // The address the browser is sent to for sign-in: the gateway redirects there
    public static Uri Identity { get; } = FromEnvironment("E2E_IDENTITY_URL", "http://localhost:5001");

    private static Uri FromEnvironment(string name, string fallback) =>
        new(Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback);

    // For a client that runs inside the Compose network (the synthetic probe), where the shop's public
    // addresses (localhost:5000 and localhost:5001, which the sign-in redirects the browser to) do not
    // exist. E2E_ADDRESS_MAP="localhost:5000=gateway:8080,localhost:5001=identity:8080" sends a request
    // for the first address to the second. Only the connection goes elsewhere: the cookies and the
    // redirects keep the public address, as they would in a browser. Unset, nothing changes.
    public static HttpMessageHandler Transport(HttpMessageHandler inner) =>
        ParseAddressMap(Environment.GetEnvironmentVariable("E2E_ADDRESS_MAP")) is { Count: > 0 } map
            ? new AddressMapHandler(map, inner)
            : inner;

    public static Dictionary<string, string> ParseAddressMap(string? setting) =>
        (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
}

public sealed class AddressMapHandler(IReadOnlyDictionary<string, string> map, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (map.TryGetValue($"{uri.Host}:{uri.Port}", out var target))
        {
            var (host, port) = target.Split(':', 2) is [var h, var p] && int.TryParse(p, out var number) ? (h, number) : (target, uri.Port);
            // The server must still see the address the browser asked for: the gateway builds the redirect
            // address of the sign-in from the Host header, and Identity only accepts the public one
            request.Headers.Host ??= uri.Authority;
            request.RequestUri = new UriBuilder(uri) { Host = host, Port = port }.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}
