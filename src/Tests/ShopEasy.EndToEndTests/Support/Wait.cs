using Xunit.Sdk;

namespace ShopEasy.EndToEndTests.Support;

// The system finishes an order in the background, so a test asks again until the answer is the
// one it expects, and says what it last saw when the time runs out.
public static class Wait
{
    // A service that was just started takes a few seconds to answer; until then the connection is
    // refused or times out, which is what is being waited out
    public static async Task UntilAnswersAsync(Uri url, string what, TimeSpan? timeout = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await UntilAsync(async () =>
        {
            try
            {
                using var response = await http.GetAsync(url);
                return (int)response.StatusCode;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                return 0;
            }
        }, status => status is >= 200 and < 300, what, timeout ?? TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1));
    }

    public static async Task<T> UntilAsync<T>(
        Func<Task<T>> read, Func<T, bool> done, string what,
        TimeSpan? timeout = null, TimeSpan? every = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var seen = await read();
            if (done(seen))
                return seen;
            if (DateTime.UtcNow >= limit)
                throw new XunitException($"Gave up waiting for {what}. Last seen: {seen}");
            await Task.Delay(every ?? TimeSpan.FromMilliseconds(1500));
        }
    }
}
