using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Application.Settlement;

internal sealed record LedgerPosting(LedgerAccountType Account, long? SellerId, decimal Vnd);

public sealed class SettlementLedger(IShopStore store, TimeProvider time, ShopSettings settings)
{
    internal static async Task Post(IShopSession db, string key, LedgerEventType type, DateTimeOffset now,
        IReadOnlyList<LedgerPosting> entries, Guid? sellerOrderId = null, Guid? paymentAttemptId = null,
        long? refundId = null, Guid? payoutId = null)
    {
        var postings = entries.GroupBy(entry => (entry.Account, entry.SellerId))
            .Select(group => new LedgerPosting(group.Key.Account, group.Key.SellerId, group.Sum(entry => entry.Vnd)))
            .Where(entry => entry.Vnd != 0)
            .OrderBy(entry => entry.Account).ThenBy(entry => entry.SellerId).ToList();
        if (postings.Count == 0 || await Posted(db, key)) return;
        if (postings.Sum(entry => entry.Vnd) != 0)
            throw new ShopRuleException("Settlement journal must balance in whole VND.");
        foreach (var posting in postings)
            ShopRules.Vnd(Math.Abs(posting.Vnd));

        var accounts = new List<LedgerAccount>();
        var created = false;
        foreach (var posting in postings)
        {
            var account = await db.First(db.Query<LedgerAccount>().Where(a => a.Type == posting.Account && a.SellerId == posting.SellerId));
            if (account is null)
            {
                account = new LedgerAccount { Type = posting.Account, SellerId = posting.SellerId };
                db.Add(account);
                created = true;
            }
            accounts.Add(account);
        }
        if (created) await db.Save();

        db.Add(new LedgerJournal
        {
            BusinessKey = key,
            Type = type,
            PostedAt = now,
            SellerOrderId = sellerOrderId,
            PaymentAttemptId = paymentAttemptId,
            RefundId = refundId,
            PayoutId = payoutId,
            Entries = postings.Select((posting, i) => new LedgerEntry
            {
                AccountId = accounts[i].Id,
                AmountVnd = Math.Abs(posting.Vnd),
                Side = posting.Vnd > 0 ? LedgerSide.Credit : LedgerSide.Debit
            }).ToList()
        });
    }

    internal static Task<decimal> Balance(IShopSession db, LedgerAccountType type, long? sellerId) =>
        db.Sum(from entry in db.Query<LedgerEntry>()
               join account in db.Query<LedgerAccount>() on entry.AccountId equals account.Id
               where account.Type == type && account.SellerId == sellerId
               select entry.Side == LedgerSide.Credit ? entry.AmountVnd : -entry.AmountVnd);

    internal static Task RecordCapture(IShopSession db, PaymentAttempt capture, DateTimeOffset now) =>
        Post(db, CaptureKey(capture.Id), LedgerEventType.Capture, now,
            [new(LedgerAccountType.PaymentClearing, null, -capture.CapturedAmountVnd),
             new(LedgerAccountType.MarketplaceReceipts, null, capture.CapturedAmountVnd)], paymentAttemptId: capture.Id);

    internal static async Task RecordSales(IShopSession db, PaymentAttempt capture, IEnumerable<SellerOrder> sellerOrders,
        DateTimeOffset now)
    {
        foreach (var order in sellerOrders.OrderBy(order => order.SellerId))
        {
            var proceeds = MerchandiseAccount(order.SellerId);
            await Post(db, SaleKey(order.Id), LedgerEventType.Sale, now,
                [new(LedgerAccountType.MarketplaceReceipts, null, -order.TotalVnd),
                 new(LedgerAccountType.PlatformDiscount, null, -order.PlatformDiscountVnd),
                 new(proceeds.Account, proceeds.SellerId, order.SubtotalVnd - order.SellerDiscountVnd),
                 new(LedgerAccountType.PlatformShipping, null, order.ShippingVnd)],
                sellerOrderId: order.Id, paymentAttemptId: capture.Id);

            await Post(db, $"commission:{order.Id:N}", LedgerEventType.Commission, now,
                [new(proceeds.Account, proceeds.SellerId, -order.CommissionVnd),
                 new(LedgerAccountType.PlatformCommission, null, order.CommissionVnd)],
                sellerOrderId: order.Id, paymentAttemptId: capture.Id);
        }
    }

    internal static async Task RecordRefund(IShopSession db, Refund refund, DateTimeOffset now)
    {
        if (refund.PaymentAttemptId is not Guid captureId) return;
        if (refund.SellerOrderId is Guid sellerOrderId)
        {
            if (!await Posted(db, SaleKey(sellerOrderId))) return;
            var order = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
                ?? throw new ShopRuleException("Seller order not found for settlement.");
            var lines = await db.List(db.Query<RefundItem>().Where(item => item.RefundId == refund.Id));
            await ReverseSale(db, order, captureId, refund.Id.ToString(), refund.AmountVnd, refund.ShippingVnd, lines, now, refund.Id);
        }
        else if (await Posted(db, CaptureKey(captureId)))
        {
            await Post(db, $"refund:{refund.Id}", LedgerEventType.Refund, now,
                [new(LedgerAccountType.MarketplaceReceipts, null, -refund.AmountVnd),
                 new(LedgerAccountType.PaymentClearing, null, refund.AmountVnd)],
                paymentAttemptId: captureId, refundId: refund.Id);
        }
    }

    internal static async Task ReverseSale(IShopSession db, SellerOrder order, Guid captureId, string key,
        decimal refundVnd, decimal shippingVnd, IReadOnlyList<RefundItem> lines, DateTimeOffset now, long? refundId = null)
    {
        if (!await Posted(db, SaleKey(order.Id))) return;
        var proceeds = MerchandiseAccount(order.SellerId);
        if (proceeds.Account == LedgerAccountType.SellerPending && await Posted(db, ReleaseKey(order.Id)))
            proceeds.Account = LedgerAccountType.SellerAvailable;

        var subsidy = lines.Sum(line => line.PlatformDiscountReversalVnd);
        var commission = lines.Sum(line => line.CommissionReversalVnd);
        var merchandise = refundVnd - shippingVnd + subsidy;
        await Post(db, $"refund:{key}", LedgerEventType.Refund, now,
            [new(proceeds.Account, proceeds.SellerId, -merchandise),
             new(LedgerAccountType.PlatformShipping, null, -shippingVnd),
             new(LedgerAccountType.PlatformDiscount, null, subsidy),
             new(LedgerAccountType.PaymentClearing, null, refundVnd)],
            sellerOrderId: order.Id, paymentAttemptId: captureId, refundId: refundId);
        await Post(db, $"commission-reversal:{key}", LedgerEventType.CommissionReversal, now,
            [new(LedgerAccountType.PlatformCommission, null, -commission),
             new(proceeds.Account, proceeds.SellerId, commission)],
            sellerOrderId: order.Id, paymentAttemptId: captureId, refundId: refundId);
    }

    public Task<int> ReleaseDue() => store.Write(async db =>
    {
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-Math.Clamp(settings.SettlementHoldDays, 0, 3650));
        var journals = db.Query<LedgerJournal>();
        var returns = db.Query<ReturnRequest>();
        var refunds = db.Query<Refund>();
        var pendingEntries = from entry in db.Query<LedgerEntry>()
                             join account in db.Query<LedgerAccount>() on entry.AccountId equals account.Id
                             join journal in journals on entry.JournalId equals journal.Id
                             where account.Type == LedgerAccountType.SellerPending && journal.SellerOrderId != null
                             select new { journal.SellerOrderId, Vnd = entry.Side == LedgerSide.Credit ? entry.AmountVnd : -entry.AmountVnd };
        var due = await db.List((from sale in journals
                                 join order in db.Query<SellerOrder>() on sale.SellerOrderId equals (Guid?)order.Id
                                 join capture in db.Query<PaymentAttempt>() on sale.PaymentAttemptId equals (Guid?)capture.Id
                                 where sale.Type == LedgerEventType.Sale && order.SellerId != Seller.FirstPartyId
                                     && order.Status == OrderStatus.Delivered && order.DeliveredAt <= cutoff
                                     && capture.Status == PaymentStatus.Paid && capture.AppliedToOrder && capture.PaidAt <= cutoff
                                     && capture.Issue == ""
                                     && !journals.Any(j => j.SellerOrderId == order.Id && j.Type == LedgerEventType.SettlementRelease)
                                     && !returns.Any(r => r.SellerOrderId == order.Id && r.Status != ReturnStatus.Completed && r.Status != ReturnStatus.Rejected)
                                     && !refunds.Any(r => r.SellerOrderId == order.Id && r.Status == RefundStatus.Pending)
                                 let amount = pendingEntries.Where(e => e.SellerOrderId == order.Id).Sum(e => e.Vnd)
                                 where amount > 0
                                 orderby order.DeliveredAt, order.Id
                                 select new { order.Id, order.SellerId, CaptureId = capture.Id, Amount = amount }).Take(25));
        foreach (var order in due)
            await Post(db, ReleaseKey(order.Id), LedgerEventType.SettlementRelease, now,
                [new(LedgerAccountType.SellerPending, order.SellerId, -order.Amount),
                 new(LedgerAccountType.SellerAvailable, order.SellerId, order.Amount)],
                sellerOrderId: order.Id, paymentAttemptId: order.CaptureId);
        return due.Count;
    });

    public Task<IReadOnlyList<SettlementPaymentRow>> RecentPayments(ShopActor actor)
    {
        actor.RequireAdmin();
        return store.Read<IReadOnlyList<SettlementPaymentRow>>(async db =>
        {
            var refunds = db.Query<Refund>();
            var journals = db.Query<LedgerJournal>();
            return await db.List((from capture in db.Query<PaymentAttempt>()
                                  join order in db.Query<Order>() on capture.OrderId equals order.Id
                                  where capture.Status == PaymentStatus.Paid
                                  orderby capture.PaidAt descending, capture.Id descending
                                  select new SettlementPaymentRow(order.Id, capture.Id, order.Number,
                                      capture.ProviderTransaction, capture.CapturedAmountVnd,
                                      refunds.Where(r => r.PaymentAttemptId == capture.Id && r.Status == RefundStatus.Pending).Sum(r => r.AmountVnd),
                                      refunds.Where(r => r.PaymentAttemptId == capture.Id && r.Status == RefundStatus.Completed).Sum(r => r.AmountVnd),
                                      capture.PaidAt, capture.AppliedToOrder,
                                      journals.Any(j => j.PaymentAttemptId == capture.Id && (j.Type == LedgerEventType.Capture || j.Type == LedgerEventType.Sale)),
                                      capture.Issue)).Take(30));
        });
    }

    public Task<IReadOnlyList<SettlementRefundRow>> RecentRefunds(ShopActor actor)
    {
        actor.RequireAdmin();
        return store.Read<IReadOnlyList<SettlementRefundRow>>(async db =>
        {
            var journals = db.Query<LedgerJournal>();
            return await db.List((from refund in db.Query<Refund>()
                                  join order in db.Query<Order>() on refund.OrderId equals order.Id
                                  orderby refund.RequestedAt descending, refund.Id descending
                                  select new SettlementRefundRow(refund.Id, order.Id, order.Number, refund.PaymentAttemptId,
                                      refund.Reference, refund.AmountVnd, refund.Status, refund.CompletedAt,
                                      journals.Any(j => j.RefundId == refund.Id && j.Type == LedgerEventType.Refund))).Take(30));
        });
    }

    private static (LedgerAccountType Account, long? SellerId) MerchandiseAccount(long sellerId) => sellerId == Seller.FirstPartyId
        ? (LedgerAccountType.PlatformMerchandise, null)
        : (LedgerAccountType.SellerPending, sellerId);

    private static string CaptureKey(Guid id) => $"capture:{id:N}";
    private static string SaleKey(Guid id) => $"sale:{id:N}";
    private static string ReleaseKey(Guid id) => $"release:{id:N}";

    private static async Task<bool> Posted(IShopSession db, string key) =>
        await db.Count(db.Query<LedgerJournal>().Where(journal => journal.BusinessKey == key)) != 0;
}
