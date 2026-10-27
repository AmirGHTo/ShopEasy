using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Services;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// Asking Payment what it holds for orders, as the consistency check does.
public class PaymentAuditClientTests
{
    private sealed class FakePayment : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "[]";
        public HttpRequestMessage? Last;
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Last = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (PaymentAuditClient Client, FakePayment Payment, ITokenService Tokens) Create()
    {
        var payment = new FakePayment();
        var tokens = Substitute.For<ITokenService>();
        tokens.GetServiceTokenAsync(Arg.Any<CancellationToken>()).Returns("payment-token");
        var client = new PaymentAuditClient(
            new HttpClient(payment) { BaseAddress = new Uri("http://payment.test") }, tokens, NullLogger<PaymentAuditClient>.Instance);
        return (client, payment, tokens);
    }

    [Fact]
    public async Task AsksForAllTheOrdersInOneRequest_WithTheServiceToken_AndReadsTheRows()
    {
        var (client, payment, _) = Create();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        payment.Body = $$"""[{"orderId":"{{first}}","status":"Succeeded","amount":42.5},{"orderId":"{{second}}","status":"Refunded","amount":10}]""";

        var rows = await client.GetPaymentsAsync([first, second]);

        rows.Should().BeEquivalentTo([new PaymentRow(first, "Succeeded", 42.5m), new PaymentRow(second, "Refunded", 10m)]);
        payment.Requests.Should().Be(1);
        payment.Last!.Headers.Authorization!.ToString().Should().Be("Bearer payment-token");
        payment.Last.RequestUri!.PathAndQuery.Should().Be($"/api/payments/audit?orderIds={first}&orderIds={second}");
    }

    [Fact]
    public async Task NoOrders_NeedNoRequestAndNoToken()
    {
        var (client, payment, tokens) = Create();

        (await client.GetPaymentsAsync([])).Should().BeEmpty();

        payment.Requests.Should().Be(0);
        await tokens.DidNotReceive().GetServiceTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoreOrdersThanPaymentTakes_AreRefusedBeforeAnythingIsSent()
    {
        var (client, payment, _) = Create();
        var ids = Enumerable.Range(0, PaymentAuditClient.MaxOrders + 1).Select(_ => Guid.NewGuid()).ToList();

        await client.Invoking(c => c.GetPaymentsAsync(ids)).Should().ThrowAsync<ArgumentException>();

        payment.Requests.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedToken_IsDropped_SoTheNextCallFetchesANewOne(HttpStatusCode refusal)
    {
        var (client, payment, tokens) = Create();
        payment.Status = refusal;

        await client.Invoking(c => c.GetPaymentsAsync([Guid.NewGuid()])).Should().ThrowAsync<HttpRequestException>();

        tokens.Received(1).Invalidate("payment-token");
    }

    [Fact]
    public async Task AFailureOnPaymentsSide_IsAnError_NotAnEmptyAnswer()
    {
        var (client, payment, tokens) = Create();
        payment.Status = HttpStatusCode.InternalServerError;

        // an empty answer would read as "no payment for any of these orders" and raise false alarms
        await client.Invoking(c => c.GetPaymentsAsync([Guid.NewGuid()])).Should().ThrowAsync<HttpRequestException>();

        tokens.DidNotReceive().Invalidate(Arg.Any<string>());
    }
}
