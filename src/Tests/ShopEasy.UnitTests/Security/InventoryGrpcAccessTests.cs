using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.InventoryService.Data;
using ShopEasy.InventoryService.Grpc;
using ShopEasy.Shared.Security;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Security;

// The gRPC stock check is for the Order service only: a token with the inventory.read scope that a
// service got for itself. The real gRPC service and the real endpoint mapping, called over HTTP/2.
public class InventoryGrpcAccessTests : IAsyncLifetime
{
    private AuthHost _inventory = null!;

    public async Task InitializeAsync() => _inventory = await AuthHost.StartAsync(
        ApiAudiences.Inventory,
        AccessPolicies.Inventory,
        http2: true,
        configure: builder =>
        {
            builder.Services.AddGrpc();
            builder.Services.AddDbContext<InventoryDbContext>(o => o.UseInMemoryDatabase("grpc-access"));
        },
        map: app => app.MapInventoryGrpc());

    public Task DisposeAsync() => _inventory.DisposeAsync().AsTask();

    // An empty GetStockBatch request: the gRPC message framing (a zero byte and a zero length)
    private async Task<(HttpStatusCode Status, string? GrpcStatus)> Call(string? token, string method = "GetStockBatch")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_inventory.Url}/inventory.InventoryGrpc/{method}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(new byte[5]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _inventory.Client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();
        var grpcStatus = response.TrailingHeaders.TryGetValues("grpc-status", out var trailer) ? trailer.Single()
            : response.Headers.TryGetValues("grpc-status", out var header) ? header.Single() : null;
        return (response.StatusCode, grpcStatus);
    }

    [Fact]
    public async Task TheOrderServicesToken_IsLetIn()
    {
        var (status, grpcStatus) = await Call(TestIdentityProvider.Principals.OrderService().Issue());

        status.Should().Be(HttpStatusCode.OK);
        grpcStatus.Should().Be("0");
    }

    [Fact]
    public async Task BothStockCallsAreProtected()
    {
        (await Call(null, "GetStock")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Call(null, "GetStockBatch")).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARequestWithoutAToken_IsRefused() =>
        (await Call(null)).Status.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task AnUnparseableToken_IsRefused() =>
        (await Call("abc.def.ghi")).Status.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task AUsersToken_IsRefused_EvenAnAdminsWithTheScope()
    {
        (await Call(TestIdentityProvider.Principals.Customer().Issue())).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Call(TestIdentityProvider.Principals.Admin().Issue())).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AServiceToken_WithoutTheScope_IsRefused()
    {
        var token = new TestIdentityProvider.Token([ApiAudiences.Inventory], [], ClientId: "order-service");

        (await Call(token.Issue())).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AServiceToken_ForAnotherApi_IsRefused()
    {
        var token = new TestIdentityProvider.Token([ApiAudiences.Payment], [ApiScopes.InventoryRead], ClientId: "order-service");

        (await Call(token.Issue())).Status.Should().Be(HttpStatusCode.Unauthorized);
    }
}
