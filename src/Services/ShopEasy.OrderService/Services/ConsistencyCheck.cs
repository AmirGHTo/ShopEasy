using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;

namespace ShopEasy.OrderService.Services;

// Every service checks its own work; this checks the result across services. It looks at the orders
// that have just settled and asks Payment what it holds for them. An order that disagrees with its
// payment (see ConsistencyRules) is reported: logged with the order and what Payment holds, and
// counted in shopeasy_consistency_violations, which raises the OrderConsistencyViolated alert.
//
// It only reads. It never changes an order or a payment: a mismatch needs a person to decide what
// is right.
//
// Why it is built this way:
//   - Each pass takes the orders that settled since the last one (a watermark), not the whole table,
//     so the work follows the number of new orders. After a restart, or failed passes, it catches up
//     on the last LookbackMinutes and not further.
//   - Orders are judged SettleSeconds after their last change, and orders whose refund or restock is
//     still under way are skipped, so ordinary compensation does not look like a fault.
//   - A mismatch counts only when two passes in a row find it. One sighting can be a race between two
//     databases read a moment apart; if it is still there a minute later it is a finding. A finding
//     stays counted, and its order is looked at again every pass, until it is put right.
//   - Payment being unreachable fails the pass (logged by GuardedPass, counted, covered by
//     PollingLoopStalled). It never reads as "Payment holds nothing".
//
// The memory of what was checked lives in the process. A restart forgets findings older than the
// lookback; with several Order replicas each one runs its own check (harmless, and the alert takes the
// maximum).
public class ConsistencyCheck(
    IServiceScopeFactory scopeFactory,
    IOptions<ConsistencyCheckOptions> options,
    ILogger<ConsistencyCheck> logger,
    IOptions<BackgroundLoopOptions>? loops = null,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly ConsistencyCheckOptions _options = options.Value;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly GuardedPass _guard = new(logger, "Consistency check", loops);

    // Orders that settled up to here have been checked
    private DateTime? _checkedUpTo;

    // (order, rule) -> how many passes in a row found the order breaking the rule
    private Dictionary<(Guid Order, string Rule), int> _findings = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        PublishViolations();

        if (!_options.Enabled)
        {
            logger.LogWarning("Consistency check is switched off ({Section}:Enabled is false)", ConsistencyCheckOptions.SectionName);
            return;
        }

        logger.LogInformation(
            "Consistency check started: every {Interval}s, orders {Settle}s after they settled, looking back at most {Lookback} min",
            _options.IntervalSeconds, _options.SettleSeconds, _options.LookbackMinutes);

        while (!ct.IsCancellationRequested)
        {
            await _guard.RunAsync(PassAsync, ct);
            await Task.Delay(_options.Interval, ct);
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await CheckAsync(
            scope.ServiceProvider.GetRequiredService<OrderDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPaymentAuditClient>(),
            ct);
    }

    // One pass. Internal so tests can run it without the timer.
    internal async Task CheckAsync(OrderDbContext db, IPaymentAuditClient payments, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var to = now - _options.Settle;
        var earliest = now - _options.Lookback;
        var from = _checkedUpTo is { } done && done > earliest ? done : earliest;
        var findingIds = _findings.Keys.Select(f => f.Order).Distinct().ToArray();
        var settled = ConsistencyRules.Settled.ToArray();
        var compensating = ConsistencyRules.Compensating.ToArray();

        var orders = await db.Orders.AsNoTracking()
            .Where(o => settled.Contains(o.Status))
            .Where(o => (o.UpdatedAt > from && o.UpdatedAt <= to) || findingIds.Contains(o.Id))
            .Where(o => !db.SagaStates.Any(s => s.OrderId == o.Id && compensating.Contains(s.Status)))
            .OrderBy(o => o.UpdatedAt)
            .Take(_options.MaxOrdersPerPass)
            .Select(o => new CheckedOrder(o.Id, o.Status, o.TotalAmount, o.UpdatedAt))
            .ToListAsync(ct);

        var held = new Dictionary<Guid, List<PaymentRow>>();
        foreach (var batch in orders.Select(o => o.Id).Chunk(PaymentAuditClient.MaxOrders))
            foreach (var row in await payments.GetPaymentsAsync(batch, ct))
            {
                if (!held.TryGetValue(row.OrderId, out var rows))
                    held[row.OrderId] = rows = [];
                rows.Add(row);
            }

        var findings = new Dictionary<(Guid Order, string Rule), int>();
        foreach (var order in orders)
        {
            var rows = held.GetValueOrDefault(order.Id) ?? [];
            foreach (var rule in ConsistencyRules.Broken(order, rows))
            {
                var passes = _findings.GetValueOrDefault((order.Id, rule)) + 1;
                findings[(order.Id, rule)] = passes;
                if (passes == 2)
                    logger.LogError(
                        "CONSISTENCY VIOLATION {Rule}: order {OrderId} is {Status} for {Total}, but Payment holds {Payments}. " +
                        "Found by two passes in a row; nothing was changed",
                        rule, order.Id, order.Status, order.Total, Describe(rows));
            }
        }

        foreach (var (order, rule) in _findings.Where(f => f.Value >= 2).Select(f => f.Key).Except(findings.Where(f => f.Value >= 2).Select(f => f.Key)))
            logger.LogInformation("Consistency violation {Rule} of order {OrderId} has been put right", rule, order);

        // Everything up to here has been looked at, unless the pass was cut short: then carry on from
        // the last order that was reached
        _checkedUpTo = orders.Count < _options.MaxOrdersPerPass
            ? to
            : orders.Where(o => o.UpdatedAt > from && o.UpdatedAt <= to).Select(o => o.UpdatedAt).DefaultIfEmpty(from).Max();
        _findings = findings;

        OrderMetrics.RecordConsistencyChecked(orders.Count);
        PublishViolations();
    }

    private void PublishViolations() => OrderMetrics.SetConsistencyViolations(
        ConsistencyRules.All.ToDictionary(rule => rule, rule => _findings.Count(f => f.Key.Rule == rule && f.Value >= 2)));

    private static string Describe(IReadOnlyList<PaymentRow> rows)
    {
        if (rows.Count == 0)
            return "nothing";
        var text = new StringBuilder();
        foreach (var row in rows)
            text.Append(text.Length == 0 ? "" : ", ").Append(row.Status).Append(' ').Append(row.Amount);
        return text.ToString();
    }
}
