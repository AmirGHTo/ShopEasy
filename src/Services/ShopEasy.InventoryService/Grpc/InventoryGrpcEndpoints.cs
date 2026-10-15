using ShopEasy.Shared.Security;

namespace ShopEasy.InventoryService.Grpc;

public static class InventoryGrpcEndpoints
{
    // The gRPC stock check is an internal call between services, not an API for users: it needs a
    // token with the inventory.read scope that a service obtained for itself (client credentials).
    // A user's token, even one that carries the scope, is not accepted here.
    public static IEndpointConventionBuilder MapInventoryGrpc(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGrpcService<InventoryGrpcService>()
            .RequireAuthorization(PolicyNames.InventoryInternal);
}
