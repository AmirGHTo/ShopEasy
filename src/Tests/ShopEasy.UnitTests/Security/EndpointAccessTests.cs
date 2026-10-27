using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// Who may call what. Every endpoint of every API is requested with each kind of caller, against the
// real controllers with the same bearer-token validation and policies the services use, and the
// answer must be the one in the table below. The table is also the model written down:
//
//   401  the caller is not identified or the token is not meant for this API
//   403  identified, but not allowed: the scope is missing, or the role is
//   ok   let in (the request reaches the controller)
//
// The callers:
//   anon         no token
//   customer     a customer through the web gateway: every scope the gateway may ask for, role customer
//   admin        an administrator through the web gateway: the same scopes, role admin
//   noScope      a customer whose client was granted no API scope
//   adminNoScope an administrator whose client was granted no API scope: the role alone is not enough
//   otherApi     a perfectly good admin token, but issued for a different API
//   service      the token the Order service gets for itself for the API in question (no user)
//
// A new endpoint that is not in the table fails EveryEndpointIsInTheTable.
public class EndpointAccessTests(ApiHosts hosts) : IClassFixture<ApiHosts>
{
    private static readonly string[] Callers = ["anon", "customer", "admin", "noScope", "adminNoScope", "otherApi", "service"];

    private sealed record Row(string Method, string Pattern, string Path, string[] Expected)
    {
        public override string ToString() => $"{Method} {Path}";
    }

    private static Row R(string method, string pattern, string path, string outcomes) =>
        new(method, pattern, path, outcomes.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private const string Id = "11111111-1111-1111-1111-111111111111";

    private static readonly Dictionary<string, Row[]> Table = new()
    {
        ["Order"] =
        [
            //                                                                       anon cust  admin noSc adSc  other svc
            R("GET",    "api/orders",                                "api/orders",                                    "401  ok   ok   403  403  401  401"),
            R("GET",    "api/orders/{orderId}",                      $"api/orders/{Id}",                              "401  ok   ok   403  403  401  401"),
            R("GET",    "api/orders/status-counts",                  "api/orders/status-counts",                      "401  ok   ok   403  403  401  401"),
            R("POST",   "api/orders",                                "api/orders",                                    "401  ok   ok   403  403  401  401"),
            R("DELETE", "api/orders/{id}",                           $"api/orders/{Id}",                              "401  ok   ok   403  403  401  401"),
            R("GET",    "api/outbox/pending",                        "api/outbox/pending",                            "401  403  ok   403  403  401  401"),
            R("GET",    "api/outbox/delivered",                      "api/outbox/delivered",                          "401  403  ok   403  403  401  401"),
            R("GET",    "api/outbox/sagas",                          "api/outbox/sagas",                              "401  403  ok   403  403  401  401"),
            R("GET",    "api/outbox/deadletters",                    "api/outbox/deadletters",                        "401  403  ok   403  403  401  401"),
            R("POST",   "api/outbox/deadletters/{queueName}/requeue","api/outbox/deadletters/some_error/requeue",     "401  403  ok   403  403  401  401"),
        ],
        ["Inventory"] =
        [
            R("GET",    "api/inventory",                             "api/inventory",                                 "401  ok   ok   403  403  401  ok"),
            R("GET",    "api/inventory/{productId}",                 $"api/inventory/{Id}",                           "401  ok   ok   403  403  401  ok"),
            R("POST",   "api/inventory/{productId}/adjust",          $"api/inventory/{Id}/adjust",                    "401  403  ok   403  403  401  403"),
            R("GET",    "api/inventory/{productId}/transactions",    $"api/inventory/{Id}/transactions",              "401  403  ok   403  403  401  403"),
        ],
        ["Product"] =
        [
            R("GET",    "api/v{version}/products",                   "api/v1/products",                               "ok   ok   ok   ok   ok   ok   ok"),
            R("GET",    "api/v{version}/products/{id}",              $"api/v1/products/{Id}",                         "ok   ok   ok   ok   ok   ok   ok"),
            R("GET",    "api/v{version}/products/categories",        "api/v1/products/categories",                    "ok   ok   ok   ok   ok   ok   ok"),
            R("GET",    "api/v{version}/products",                   "api/v2/products",                               "ok   ok   ok   ok   ok   ok   ok"),
            R("GET",    "api/v{version}/products/{id}",              $"api/v2/products/{Id}",                         "ok   ok   ok   ok   ok   ok   ok"),
            R("POST",   "api/v{version}/products",                   "api/v1/products",                               "401  403  ok   403  403  401  401"),
            R("PUT",    "api/v{version}/products/{id}",              $"api/v1/products/{Id}",                         "401  403  ok   403  403  401  401"),
            R("DELETE", "api/v{version}/products/{id}",              $"api/v1/products/{Id}",                         "401  403  ok   403  403  401  401"),
        ],
        ["Payment"] =
        [
            // (a service gets past the scope here; the controller still refuses it, see PaymentsControllerTests)
            R("GET",    "api/payments/order/{orderId}",              $"api/payments/order/{Id}",                      "401  ok   ok   403  403  401  ok"),
            R("GET",    "api/payments",                              "api/payments",                                  "401  403  ok   403  403  401  403"),
            R("GET",    "api/payments/audit",                        $"api/payments/audit?orderIds={Id}",             "401  403  403  403  403  401  ok"),
        ],
    };

    private static string TokenFor(string api, string caller) => caller switch
    {
        "anon" => null!,
        "customer" => TestIdentityProvider.Principals.Customer().Issue(),
        "admin" => TestIdentityProvider.Principals.Admin().Issue(),
        "noScope" => TestIdentityProvider.Principals.CustomerWithoutScopes().Issue(),
        "adminNoScope" => TestIdentityProvider.Principals.AdminWithoutScopes().Issue(),
        "otherApi" => TestIdentityProvider.Principals.ForAnotherApi(Audience(api)).Issue(),
        "service" => api == "Payment"
            ? TestIdentityProvider.Principals.OrderServiceForPayment().Issue()
            : TestIdentityProvider.Principals.OrderService().Issue(),
        _ => throw new ArgumentOutOfRangeException(nameof(caller)),
    };

    internal static string Audience(string api) => api switch
    {
        "Order" => ApiAudiences.Order,
        "Inventory" => ApiAudiences.Inventory,
        "Product" => ApiAudiences.Product,
        "Payment" => ApiAudiences.Payment,
        _ => throw new ArgumentOutOfRangeException(nameof(api)),
    };

    public static TheoryData<string, int> Endpoints()
    {
        var data = new TheoryData<string, int>();
        foreach (var (api, rows) in Table)
            for (var i = 0; i < rows.Length; i++)
                data.Add(api, i);
        return data;
    }

    public static TheoryData<string> Apis() => new(Table.Keys);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EachKindOfCaller_IsTreatedAsTheTableSays(string api, int index)
    {
        var row = Table[api][index];
        var host = hosts.For(api);
        var mismatches = new List<string>();

        for (var i = 0; i < Callers.Length; i++)
        {
            var status = await host.SendAsync(new HttpMethod(row.Method), row.Path, TokenFor(api, Callers[i]));
            var expected = row.Expected[i];
            var matches = expected switch
            {
                "401" => status == HttpStatusCode.Unauthorized,
                "403" => status == HttpStatusCode.Forbidden,
                // routed and not turned away: the request reached the controller
                "ok" => status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed),
                _ => throw new InvalidOperationException($"unknown outcome {expected}"),
            };
            if (!matches)
                mismatches.Add($"{Callers[i]}: expected {expected}, got {(int)status}");
        }

        mismatches.Should().BeEmpty($"{row} must treat each caller as the table says");
    }

    // A missing token is answered with a challenge, a token without the right to be there with a refusal
    [Fact]
    public async Task ARequestWithoutAToken_IsChallengedForBearerTokens()
    {
        using var response = await hosts.For("Order").Client.GetAsync("api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle(h => h.Scheme == "Bearer");
    }

    private static string Normalize(string pattern) =>
        Regex.Replace(pattern.Trim('/'), @"\{([^}:]+)(:[^}]*)?\}", "{$1}").ToLowerInvariant();

    [Theory]
    [MemberData(nameof(Apis))]
    public void EveryEndpointIsInTheTable(string api)
    {
        var inTheService = hosts.For(api).Endpoints
            .Select(e => $"{e.Method} {Normalize(e.Pattern)}")
            .ToHashSet();
        var inTheTable = Table[api]
            .Select(r => $"{r.Method} {r.Pattern.ToLowerInvariant()}")
            .ToHashSet();

        inTheService.Except(inTheTable).Should().BeEmpty(
            "an endpoint that is not in the access table has not been looked at: decide who may call it and add a row");
        inTheTable.Except(inTheService).Should().BeEmpty("the table lists an endpoint the service does not have");
    }
}
