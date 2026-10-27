using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopEasy.PaymentService.Controllers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Payments;

public class PaymentsControllerTests : IDisposable
{
    private readonly PaymentDbContext _db = new(new DbContextOptionsBuilder<PaymentDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    // A controller called by the given caller: a service has a client id and no subject
    private PaymentsController As(params Claim[] claims) => new(_db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        }
    };

    private PaymentsController AsService() => As(new Claim("client_id", "order-service"));

    private async Task<Guid> Pay(PaymentStatus status, decimal amount, string userId = "user-1")
    {
        var orderId = Guid.NewGuid();
        _db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, UserId = userId, Amount = amount, Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return orderId;
    }

    private static List<PaymentAuditEntry> Entries(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IEnumerable<PaymentAuditEntry>>()
            .Subject.ToList();

    // ── the audit for the Order service's consistency check ──────────────────

    [Fact]
    public async Task TheAudit_AnswersWithTheStatusAndAmountOfEachOrdersPayment()
    {
        var paid = await Pay(PaymentStatus.Succeeded, 42.50m);
        var refunded = await Pay(PaymentStatus.Refunded, 10m);
        await Pay(PaymentStatus.Failed, 99m); // an order nobody asked about

        var entries = Entries(await AsService().Audit([paid, refunded]));

        entries.Should().BeEquivalentTo(
        [
            new PaymentAuditEntry(paid, "Succeeded", 42.50m),
            new PaymentAuditEntry(refunded, "Refunded", 10m)
        ]);
    }

    [Fact]
    public async Task AnOrderWithoutAPayment_IsSimplyNotInTheAnswer()
    {
        var paid = await Pay(PaymentStatus.Succeeded, 5m);

        var entries = Entries(await AsService().Audit([paid, Guid.NewGuid()]));

        entries.Should().ContainSingle().Which.OrderId.Should().Be(paid);
    }

    [Fact]
    public async Task ASecondPaymentForTheSameOrder_ShowsUp()
    {
        var orderId = await Pay(PaymentStatus.Succeeded, 5m);
        _db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, UserId = "user-1", Amount = 5m, Status = PaymentStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var entries = Entries(await AsService().Audit([orderId]));

        entries.Should().HaveCount(2, "the check can only find a double charge if the audit does not hide it");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PaymentsController.MaxAuditOrders + 1)]
    public async Task TheAudit_RefusesNoOrdersAndTooManyOrders(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

        var result = await AsService().Audit(ids);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TheAudit_TakesTheMostItAllows()
    {
        var ids = Enumerable.Range(0, PaymentsController.MaxAuditOrders).Select(_ => Guid.NewGuid()).ToArray();

        (await AsService().Audit(ids)).Should().BeOfType<OkObjectResult>();
    }

    // ── a service token reaches the endpoints made for customers (it carries payment.read) and is turned away there

    [Fact]
    public async Task AServiceToken_CannotReadACustomersPayment()
    {
        var orderId = await Pay(PaymentStatus.Succeeded, 5m);

        var result = await AsService().GetByOrder(orderId);

        result.Should().BeOfType<ForbidResult>("a token without a user is nobody's payment");
    }
}
