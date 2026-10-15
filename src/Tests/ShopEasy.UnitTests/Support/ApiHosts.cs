using Asp.Versioning;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.OrderService.Controllers;
using ShopEasy.PaymentService.Controllers;
using ShopEasy.Shared.Security;
using Xunit;

namespace ShopEasy.UnitTests.Support;

// The four APIs of the shop, started once for a test class (see AuthHost).
public sealed class ApiHosts : IAsyncLifetime
{
    private readonly Dictionary<string, AuthHost> _hosts = new();

    internal AuthHost For(string api) => _hosts[api];

    public async Task InitializeAsync()
    {
        _hosts["Order"] = await AuthHost.StartAsync(
            ApiAudiences.Order, AccessPolicies.Order, typeof(OrderCommandsController).Assembly);

        _hosts["Inventory"] = await AuthHost.StartAsync(
            ApiAudiences.Inventory, AccessPolicies.Inventory, typeof(ShopEasy.InventoryService.Controllers.InventoryController).Assembly);

        _hosts["Payment"] = await AuthHost.StartAsync(
            ApiAudiences.Payment, AccessPolicies.Payment, typeof(PaymentsController).Assembly);

        _hosts["Product"] = await AuthHost.StartAsync(
            ApiAudiences.Product, AccessPolicies.Product, typeof(ShopEasy.ProductService.Controllers.ProductsController).Assembly,
            configure: builder => builder.Services
                .AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ApiVersionReader = ApiVersionReader.Combine(new UrlSegmentApiVersionReader(), new HeaderApiVersionReader("X-Api-Version"));
                })
                .AddMvc());
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts.Values)
            await host.DisposeAsync();
    }
}
