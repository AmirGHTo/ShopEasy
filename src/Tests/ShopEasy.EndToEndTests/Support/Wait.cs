using Xunit.Sdk;

namespace ShopEasy.EndToEndTests.Support;

// The system finishes an order in the background, so a test asks again until the answer is the
// one it expects, and says what it last saw when the time runs out.
public static class Wait
{
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
