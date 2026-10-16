namespace ShopEasy.Shared.Security;

// The names Identity issues tokens with and the APIs check them against. They are the contract
// between the token issuer and every service, so they live in one place: Identity builds its API
// resources and client scopes from them, each service validates its own audience and its endpoints
// demand the scopes below.
//
//   audience - which API a token is meant for. A token whose audience does not include the API is
//              rejected with 401, however valid it is otherwise (a token issued for Inventory
//              cannot be used against Payment).
//   scope    - what the client was allowed to do with it. A missing scope is 403.
//   role     - what the user may do; combined with a scope ("scope AND role"), never instead of it.
public static class ApiAudiences
{
    public const string Product = "product-api";
    public const string Order = "order-api";
    public const string Inventory = "inventory-api";
    public const string Payment = "payment-api";

    public static IReadOnlyList<string> All { get; } = [Product, Order, Inventory, Payment];
}

public static class ApiScopes
{
    public const string ProductsRead = "products.read";
    public const string ProductsWrite = "products.write";
    public const string OrdersRead = "orders.read";
    public const string OrdersWrite = "orders.write";
    public const string InventoryRead = "inventory.read";
    public const string InventoryWrite = "inventory.write";
    public const string PaymentRead = "payment.read";

    // What the web gateway (the browser's backend) may ask for on behalf of a signed-in user.
    public static IReadOnlyList<string> Web { get; } =
        [ProductsRead, ProductsWrite, OrdersRead, OrdersWrite, InventoryRead, InventoryWrite, PaymentRead];

    // What the mobile app may ask for: the customer's own shopping, without the catalog or stock
    // administration scopes (they would be useless to a customer anyway: those endpoints also
    // need the admin role, but a public client should not hold them at all).
    public static IReadOnlyList<string> Mobile { get; } =
        [ProductsRead, OrdersRead, OrdersWrite, InventoryRead, PaymentRead];

    // What the Order service may ask for as itself (client credentials): reading stock levels for
    // the stock check. Nothing else.
    public static IReadOnlyList<string> OrderService { get; } = [InventoryRead];
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Customer = "customer";
}

// The claims the APIs read, as Identity issues them (inbound claim mapping is switched off, so the
// names are the ones in the token).
public static class ClaimNames
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Scope = "scope";
    public const string ClientId = "client_id";
    public const string Email = "email";
    public const string Name = "name";
}
