using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Orders;

namespace HKShop.Application.Reporting;

public sealed record SellerAuditEntry(string? ActorId, string Action, string Target, DateTimeOffset OccurredAt);
public sealed record SellerReport(SellerWorkspace Seller, SalesSummary Sales, Page<SellerAuditEntry>? Audit);

public sealed class ReportingService(IShopStore store)
{
    public Task<SalesSummary> Sales(ShopActor actor, DateOnly start, DateOnly end)
    {
        actor.RequireAdmin();
        var (from, to) = Range(start, end);
        return store.Read(db => ReadSales(db, from, to));
    }

    public Task<Page<AuditEntry>> Audit(ShopActor actor, DateOnly start, DateOnly end, string? action, int page)
    {
        actor.RequireAdmin();
        var (from, to) = Range(start, end);
        return store.Read(db => ReadAudit(db, AuditQuery(db, from, to, action), page));
    }

    public Task<SalesSummary> SellerSales(ShopActor actor, long sellerId, DateOnly start, DateOnly end) =>
        store.Write(async db =>
        {
            var seller = await SellerAccess.Require(db, actor, sellerId, SellerPermission.Finance);
            var (from, to) = Range(start, end);
            return await ReadSales(db, from, to, seller.Id);
        });

    public Task<SellerReport> SellerReport(ShopActor actor, long sellerId, DateOnly start, DateOnly end, string? action, int page) =>
        store.Write(async db =>
        {
            var seller = await SellerAccess.Require(db, actor, sellerId, SellerPermission.Finance);
            var (from, to) = Range(start, end);
            var sales = await ReadSales(db, from, to, seller.Id);
            Page<SellerAuditEntry>? audit = null;
            if (seller.Allows(SellerPermission.Audit))
            {
                var entries = AuditQuery(db, from, to, action).Where(a => a.SellerId == seller.Id)
                    .Select(a => new SellerAuditEntry(a.ActorId, a.Action, a.Target, a.OccurredAt));
                audit = await ReadAudit(db, entries, page);
            }
            return new SellerReport(seller, sales, audit);
        });

    public Task<List<OutboxMessage>> FailedMail(ShopActor actor)
    {
        actor.RequireAdmin();
        return store.Read(db => db.List(db.Query<OutboxMessage>()
            .Where(m => m.SentAt == null && m.Attempts >= 5).OrderByDescending(m => m.Id).Take(100)));
    }

    private static async Task<SalesSummary> ReadSales(IShopSession db, DateTimeOffset from, DateTimeOffset to, long? sellerId = null)
    {
        if (sellerId is null)
        {
            var orders = db.Query<Order>();
            var payments = db.Query<Payment>();
            var captures = db.Query<PaymentAttempt>().Where(a => a.Status == PaymentStatus.Paid);
            var refunds = db.Query<Refund>();
            var captured = await db.Sum(captures.Where(a => a.PaidAt >= from && a.PaidAt < to)
                .Select(a => a.CapturedAmountVnd));
            var legacyPaid = await db.Sum(payments.Where(p => p.PaidAt >= from && p.PaidAt < to &&
                !captures.Any(a => a.OrderId == p.OrderId && a.AppliedToOrder)).Select(p => p.AmountVnd));
            var refunded = await db.Sum(refunds.Where(r => r.CompletedAt >= from && r.CompletedAt < to).Select(r => r.AmountVnd));
            var groups = await db.List(orders.Where(o => o.CreatedAt >= from && o.CreatedAt < to)
                .GroupBy(o => o.Status).Select(g => new StatusCount(g.Key, g.Count())));
            return new SalesSummary(captured + legacyPaid, refunded, groups);
        }

        var sellerOrders = db.Query<SellerOrder>().Where(s => s.SellerId == sellerId.Value);
        var paidFromSellerOrders = await db.Sum(
            sellerOrders.Join(db.Query<Payment>().Where(p => p.PaidAt >= from && p.PaidAt < to),
                so => so.OrderId, p => p.OrderId, (so, _) => so.TotalVnd));

        var legacyOrders = db.Query<Order>().Where(o => o.SellerId == sellerId.Value
            && !db.Query<SellerOrder>().Any(so => so.OrderId == o.Id));
        var paidLegacy = await db.Sum(
            legacyOrders.Join(db.Query<Payment>().Where(p => p.PaidAt >= from && p.PaidAt < to),
                o => o.Id, p => p.OrderId, (_, p) => p.AmountVnd));

        var refundedSeller = await db.Sum(db.Query<Refund>()
            .Where(r => r.CompletedAt >= from && r.CompletedAt < to
                && (r.PaymentAttemptId == null || db.Query<PaymentAttempt>().Any(a =>
                    a.Id == r.PaymentAttemptId && a.AppliedToOrder))
                && ((r.SellerOrderId != null && sellerOrders.Any(so => so.Id == r.SellerOrderId))
                    || (r.SellerOrderId == null && db.Query<Order>().Any(o => o.Id == r.OrderId && o.SellerId == sellerId.Value))))
            .Select(r => r.AmountVnd));

        var soGroups = await db.List(sellerOrders.Where(so => so.CreatedAt >= from && so.CreatedAt < to)
            .GroupBy(so => so.Status).Select(g => new StatusCount(g.Key, g.Count())));
        var legacyGroups = await db.List(legacyOrders.Where(o => o.CreatedAt >= from && o.CreatedAt < to)
            .GroupBy(o => o.Status).Select(g => new StatusCount(g.Key, g.Count())));
        var mergedGroups = soGroups.Concat(legacyGroups)
            .GroupBy(g => g.Status)
            .Select(g => new StatusCount(g.Key, g.Sum(x => x.Count)))
            .ToList();

        return new SalesSummary(paidFromSellerOrders + paidLegacy, refundedSeller, mergedGroups);
    }

    private static IQueryable<AuditEntry> AuditQuery(IShopSession db, DateTimeOffset from, DateTimeOffset to, string? action)
    {
        var query = db.Query<AuditEntry>().Where(a => a.OccurredAt >= from && a.OccurredAt < to);
        if (action is not null)
        {
            var matchingKeys = AuditActionText.MatchingKeys(action);
            query = matchingKeys.Length == 0
                ? query.Where(a => a.Action.Contains(action))
                : query.Where(a => a.Action.Contains(action) || matchingKeys.Contains(a.Action));
        }
        return query.OrderByDescending(a => a.Id);
    }

    private static async Task<Page<T>> ReadAudit<T>(IShopSession db, IQueryable<T> query, int page)
    {
        const int size = 25;
        var count = await db.Count(query);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
        return new Page<T>(await db.List(query.Skip((number - 1) * size).Take(size)), number, count, size);
    }

    private static (DateTimeOffset From, DateTimeOffset To) Range(DateOnly start, DateOnly end)
    {
        if (end < start || start == DateOnly.MinValue || end == DateOnly.MaxValue || end.DayNumber - start.DayNumber > 366)
            throw new ShopRuleException("The report date range is invalid.");
        var zone = TimeSpan.FromHours(7);
        return (new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), zone),
            new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
    }
}
