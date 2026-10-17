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
}
