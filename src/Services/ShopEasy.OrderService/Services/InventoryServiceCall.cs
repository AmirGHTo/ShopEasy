using Grpc.Core;

namespace ShopEasy.OrderService.Services;

// A call to the Inventory service, made as the Order service itself: it carries the service token
// from Identity (client credentials) and runs inside InventoryCallPolicy (deadline, retry after
// connection failures, circuit breaker). Failures surface as InventoryUnavailableException.
//
// The generated gRPC client is not involved here, so this is what the tests exercise.
public class InventoryServiceCall(InventoryCallPolicy policy, ITokenService tokens, ILogger<InventoryServiceCall> logger)
{
    // The delegate receives the call metadata (with the Authorization header), the deadline of the
    // attempt and a token that is cancelled at the deadline or when the caller gives up.
    public async Task<T> ExecuteAsync<T>(
        Func<Metadata, DateTime, CancellationToken, Task<T>> call, CancellationToken ct = default)
    {
        // Fetched before the call (it is cached for most of its 15 minutes) and not inside the
        // attempt: Identity being slow or down must not eat into Inventory's deadline or count as
        // an Inventory failure for the circuit breaker.
        string token;
        try
        {
            token = await tokens.GetServiceTokenAsync(ct);
        }
        catch (ServiceTokenUnavailableException ex)
        {
            throw new InventoryUnavailableException("No service token for the Inventory call", ex);
        }

        var headers = new Metadata { { "authorization", $"Bearer {token}" } };

        try
        {
            return await policy.ExecuteAsync((deadline, attempt) => call(headers, deadline, attempt), ct);
        }
        catch (InventoryUnavailableException ex) when (ex.InnerException is RpcException
            { StatusCode: StatusCode.Unauthenticated or StatusCode.PermissionDenied } rejected)
        {
            // The token was refused (Identity rotated its keys, or the client lost the scope).
            // Forget it so that the next call asks for a new one, and say so loudly: until this
            // is fixed the stock check runs on the cached levels alone.
            tokens.Invalidate(token);
            logger.LogError(rejected,
                "Inventory refused the Order service's token ({Status}); check the order-service client in Identity",
                rejected.StatusCode);
            throw;
        }
    }
}
