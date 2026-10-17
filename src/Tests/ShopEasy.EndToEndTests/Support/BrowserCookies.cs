namespace ShopEasy.EndToEndTests.Support;

// The cookie jar of a browser talking to localhost. The sign-in cookies of the gateway are marked
// Secure, which a browser still accepts and returns from http://localhost; the CookieContainer of
// .NET would refuse to send them over http, and the sign-in would fail with "correlation failed".
// Like a browser it keeps cookies per host (not per port) and sends them by path.
public sealed class BrowserCookies(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly List<StoredCookie> _jar = [];

    private sealed record StoredCookie(string Name, string Value, string Path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var sent = string.Join("; ", _jar.Where(c => Covers(c.Path, path)).Select(c => $"{c.Name}={c.Value}"));
        if (sent.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", sent);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            foreach (var header in headers)
                Remember(header, path);
        return response;
    }

    private void Remember(string setCookie, string requestPath)
    {
        var parts = setCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var pair = parts[0].Split('=', 2);
        if (pair.Length != 2)
            return;

        var path = "/";
        var expired = false;
        foreach (var attribute in parts.Skip(1))
        {
            var keyValue = attribute.Split('=', 2);
            if (keyValue.Length != 2)
                continue;
            switch (keyValue[0].ToLowerInvariant())
            {
                case "path": path = keyValue[1]; break;
                case "max-age": expired |= int.TryParse(keyValue[1], out var seconds) && seconds <= 0; break;
                case "expires": expired |= DateTimeOffset.TryParse(keyValue[1], out var when) && when < DateTimeOffset.UtcNow; break;
            }
        }

        _jar.RemoveAll(c => c.Name == pair[0] && c.Path == path);
        if (!expired)
            _jar.Add(new StoredCookie(pair[0], pair[1], path));
    }

    private static bool Covers(string cookiePath, string requestPath) =>
        cookiePath == "/" || requestPath == cookiePath || requestPath.StartsWith(cookiePath.TrimEnd('/') + "/", StringComparison.Ordinal);
}
