namespace ShopEasy.Gateway.Resilience;

// Settings under "Resilience:Gateway".
public class GatewayResilienceOptions
{
    public const string SectionName = "Resilience:Gateway";

    // How long a service may stay silent before the gateway gives up on it: no response headers
    // within this time, or no further bytes of the request or response. The caller then gets 504
    // Gateway Timeout and the request to the service is cancelled, so a hung service does not hold
    // gateway connections (and its own) open without end. Applied to every cluster that does not set
    // its own HttpRequest:ActivityTimeout.
    public int ActivityTimeoutSeconds { get; set; } = 30;

    // Opening a connection to a service that does not answer is cut off after this long.
    public int ConnectTimeoutSeconds { get; set; } = 5;

    // A pooled connection to a service is replaced after this long. This is what makes the gateway
    // notice new replicas of a scaled service behind one DNS name; without a limit a connection is
    // kept as long as it works and the load stays on the replicas that were there first.
    public int PooledConnectionLifetimeSeconds { get; set; } = 120;

    // Retries of a GET (or HEAD) request after a connection failure, i.e. a request that never
    // reached the service or got no answer. Other methods are never retried: the service may
    // have processed them.
    public int GetRetries { get; set; } = 2;

    public void Validate()
    {
        void Within(string name, int value, int min, int max)
        {
            if (value < min || value > max)
                throw new InvalidOperationException($"{SectionName}:{name} must be between {min} and {max}, not {value}.");
        }

        Within(nameof(ActivityTimeoutSeconds), ActivityTimeoutSeconds, 1, 600);
        Within(nameof(ConnectTimeoutSeconds), ConnectTimeoutSeconds, 1, 60);
        Within(nameof(PooledConnectionLifetimeSeconds), PooledConnectionLifetimeSeconds, 10, 3600);
        Within(nameof(GetRetries), GetRetries, 0, 5);
    }
}
