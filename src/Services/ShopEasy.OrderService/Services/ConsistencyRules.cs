using ShopEasy.OrderService.Data;

namespace ShopEasy.OrderService.Services;

// An order that has settled, as the Order service knows it
public record CheckedOrder(Guid Id, OrderStatus Status, decimal Total, DateTime UpdatedAt);

// What must be true of a settled order, given what Payment holds for it. Each rule has a name that is
// the label of the shopeasy_consistency_violations metric and appears in the log.
//
// A fault in the system would leave one of these behind: an order marked paid whose payment never
// happened (goods for free), a customer charged for an order that was cancelled (money kept), a
// charge that is not what the order costs, a customer charged twice.
public static class ConsistencyRules
{
    public const string ConfirmedNotPaid = "confirmed_not_paid";
    public const string AmountDiffers = "amount_differs";
    public const string PaidTwice = "paid_twice";
    public const string CancelledStillCharged = "cancelled_still_charged";

    public static IReadOnlyList<string> All { get; } =
        [ConfirmedNotPaid, AmountDiffers, PaidTwice, CancelledStillCharged];

    // The statuses that are checked: the order is over, one way or the other
    public static IReadOnlyList<OrderStatus> Settled { get; } =
        [OrderStatus.Confirmed, OrderStatus.Shipped, OrderStatus.Delivered, OrderStatus.Cancelled, OrderStatus.PaymentFailed];

    // The saga states in which a refund, a restock or a release is still being carried out. An order
    // in one of them is between two consistent states, so it is not judged yet.
    public static IReadOnlyList<SagaStatus> Compensating { get; } =
        [SagaStatus.ReleasingInventory, SagaStatus.RefundingPayment, SagaStatus.RestockingInventory];

    public static IReadOnlyList<string> Broken(CheckedOrder order, IReadOnlyList<PaymentRow> payments)
    {
        var charges = payments.Where(p => p.Status == "Succeeded").ToList();
        var broken = new List<string>();

        if (charges.Count > 1)
            broken.Add(PaidTwice);

        switch (order.Status)
        {
            case OrderStatus.Confirmed or OrderStatus.Shipped or OrderStatus.Delivered:
                if (charges.Count == 0)
                    broken.Add(ConfirmedNotPaid);
                else if (charges.Count == 1 && charges[0].Amount != order.Total)
                    broken.Add(AmountDiffers);
                break;

            case OrderStatus.Cancelled or OrderStatus.PaymentFailed:
                // refunded or never charged is right; a charge that is still standing is not
                if (charges.Count > 0)
                    broken.Add(CancelledStillCharged);
                break;
        }

        return broken;
    }
}
