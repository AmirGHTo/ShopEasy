using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace ShopEasy.EndToEndTests.Support;

// A customer using the shop the way a browser does: creates an account on the sign-up page of
// Identity, signs in to the gateway through the real OpenID Connect redirects, and from then on
// talks to the system only through the gateway with the session cookie it was given. No token is
// ever handled here, which is the point of the gateway.
public sealed class Shopper : IDisposable
{
    private static readonly Regex AntiforgeryToken =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly BrowserCookies _cookies;

    private Shopper((HttpClient Http, BrowserCookies Cookies) browser, string email)
    {
        _http = browser.Http;
        _cookies = browser.Cookies;
        Email = email;
    }

    public string Email { get; }
    public string UserId { get; private set; } = "";

    // A browser that has no account and no session
    public static Shopper Visitor() => new(NewBrowser(), "");

    public static async Task<Shopper> SignUpAsync(string label)
    {
        var email = $"e2e-{label}-{Guid.NewGuid():N}@example.com";
        var shopper = new Shopper(NewBrowser(), email);
        await shopper.RegisterAsync(password: $"Aa1-{Guid.NewGuid():N}");
        await shopper.SignInToTheShopAsync();
        return shopper;
    }

    private static (HttpClient, BrowserCookies) NewBrowser()
    {
        var cookies = new BrowserCookies(new HttpClientHandler
        {
            AllowAutoRedirect = false, // the redirects are followed by hand, so that each one is checked
            UseCookies = false,        // the cookies are kept the way a browser keeps them, see BrowserCookies
        });
        return (new HttpClient(cookies) { Timeout = TimeSpan.FromSeconds(30) }, cookies);
    }

    // What a restart of the gateway does to a customer whose session it cannot read any more: the
    // browser still sends the cookie, the gateway does not accept it. Dropping it here is the same
    // for the rest of the shop, and leaves Identity's cookie, which is the one that matters next.
    public void LoseTheShopSession() => _cookies.Forget(name => name.StartsWith("shopeasy.session", StringComparison.Ordinal));

    // Where the gateway's sign-in sends a browser that is already signed in
    public async Task<string?> WhereSignInLeadsAsync(string returnUrl)
    {
        using var response = await _http.GetAsync(
            new Uri(Stack.Gateway, "/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        return response.Headers.Location?.OriginalString;
    }

    // ── the API, through the gateway ─────────────────────────────────────────────────────────
    public Task<Reply> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<Reply> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Post, path, body, idempotencyKey);

    // What a page of another site could make the browser send: the cookie, but not the header
    // the shop's own pages add to every request that changes something
    public Task<Reply> PostWithoutTheShopsHeaderAsync(string path, object body) =>
        SendAsync(HttpMethod.Post, path, body, theShopsHeader: false);

    private async Task<Reply> SendAsync(
        HttpMethod method, string path, object? body = null, string? idempotencyKey = null, bool theShopsHeader = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(Stack.Gateway, path));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (theShopsHeader)
            request.Headers.Add("X-Requested-With", "ShopEasy.EndToEndTests");
        using var response = await _http.SendAsync(request);
        return await Reply.ReadAsync(response);
    }

    // ── sign-up and sign-in, as a browser does them ──────────────────────────────────────────
    private async Task RegisterAsync(string password)
    {
        var page = new Uri(Stack.Identity, "/Account/Register?returnUrl=%2F");
        var form = await _http.GetStringAsync(page);
        var token = AntiforgeryToken.Match(form) is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidOperationException("The sign-up page has no form to submit: " + Excerpt(form));

        using var response = await _http.PostAsync(page, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.FirstName"] = "End",
            ["Input.LastName"] = "ToEnd",
            ["Input.Email"] = Email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        }));
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException(
                $"Sign-up was not accepted ({(int)response.StatusCode}): " + Excerpt(await response.Content.ReadAsStringAsync()));
    }

    // Identity has just signed this browser in, so the gateway's sign-in comes straight back with a
    // session cookie. /account/me then says who the gateway thinks this is. Also what a customer
    // does when the gateway has lost their session: the browser still has Identity's cookie, so no
    // password is asked for.
    public async Task SignInToTheShopAsync()
    {
        var hops = new List<string>();
        using var landed = await BrowseAsync(new Uri(Stack.Gateway, "/account/login?returnUrl=%2F"), hops);
        var me = await GetAsync("/account/me");
        if (me.Status != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"Signed in, but the gateway does not know this user: {me}. The sign-in went through {string.Join(" -> ", hops)}");
        UserId = me.Json.Text("userId");
    }

    // Opens a page the way a browser does: follows the redirects, and submits the form that
    // Identity answers the authorize request with (the code comes back to the gateway as a POST,
    // and a browser does that by running the page's script).
    private async Task<HttpResponseMessage> BrowseAsync(Uri start, List<string> hops)
    {
        var response = await FollowRedirectsAsync(await _http.GetAsync(start), start, hops);
        for (var posts = 0; posts < 3; posts++)
        {
            var page = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : "";
            if (!page.Contains("document.forms[0].submit()"))
                return response;
            response.Dispose();

            var action = new Uri(WebUtility.HtmlDecode(Regex.Match(page, "<form[^>]*action=['\"]([^'\"]+)['\"]").Groups[1].Value));
            var fields = Regex.Matches(page, "<input[^>]*name=['\"]([^'\"]+)['\"][^>]*value=['\"]([^'\"]*)['\"]")
                .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
            response = await FollowRedirectsAsync(
                await _http.PostAsync(action, new FormUrlEncodedContent(fields)), action, hops);
        }
        return response;
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(HttpResponseMessage response, Uri current, List<string> hops)
    {
        for (var hop = 0; hop < 15; hop++)
        {
            hops.Add($"{(int)response.StatusCode} {current.GetLeftPart(UriPartial.Path)}");
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response = await _http.GetAsync(current);
                continue;
            }
            return response;
        }
        throw new InvalidOperationException($"More than 15 redirects, the last at {current}");
    }

    // The error box of a Razor page, or the start of the page when there is none
    private static string Excerpt(string html)
    {
        var box = Regex.Match(html, "<div class=\"[^\"]*error[^\"]*\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
        var text = Regex.Replace(box.Success ? box.Groups[1].Value : html, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    public void Dispose() => _http.Dispose();
}
