using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Application.Settlement;

public sealed record StatementEntry(LedgerEventType EventType, DateTimeOffset OccurredAt, LedgerAccountType AccountType,
    decimal AmountVnd, Guid? SellerOrderId, string? SellerOrderNumber, long? RefundId, Guid? PayoutId);

public sealed record SellerStatement(SellerWorkspace Seller, decimal PendingVnd, decimal AvailableVnd, decimal InTransitVnd,
    Page<StatementEntry> Entries, IReadOnlyList<Payout> Payouts);

public sealed record PayoutQueueRow(Payout Payout, string SellerName);

public sealed class PayoutService(IShopStore store, IPayoutGateway gateway, TimeProvider time)
{
    private const int PageSize = 25;

    public Task<SellerStatement> Statement(ShopActor actor, long sellerId, int page) => store.Write(async db =>
    {
        var seller = await SellerAccess.Require(db, actor, sellerId, SellerPermission.Finance);
        var query = from account in db.Query<LedgerAccount>()
                    where account.SellerId == sellerId
                    join entry in db.Query<LedgerEntry>() on account.Id equals entry.AccountId
                    join journal in db.Query<LedgerJournal>() on entry.JournalId equals journal.Id
                    join order in db.Query<SellerOrder>() on journal.SellerOrderId equals (Guid?)order.Id into orders
                    from order in orders.DefaultIfEmpty()
                    orderby journal.PostedAt descending, entry.Id descending
                    select new StatementEntry(journal.Type, journal.PostedAt, account.Type,
                        entry.Side == LedgerSide.Credit ? entry.AmountVnd : -entry.AmountVnd,
                        journal.SellerOrderId, order == null ? null : order.Number, journal.RefundId, journal.PayoutId);
        var entries = await ReadPage(db, query, page);
        var payouts = await db.List(db.Query<Payout>().Where(p => p.SellerId == sellerId)
            .OrderByDescending(p => p.RequestedAt).ThenByDescending(p => p.Id).Take(PageSize));
        return new SellerStatement(seller,
            await SettlementLedger.Balance(db, LedgerAccountType.SellerPending, sellerId),
            await SettlementLedger.Balance(db, LedgerAccountType.SellerAvailable, sellerId),
            await SettlementLedger.Balance(db, LedgerAccountType.SellerPayoutInTransit, sellerId),
            entries, payouts);
    });

    public Task<Guid> Request(ShopActor actor, long sellerId, Guid submissionKey, decimal amount) =>
        store.Write(async db =>
        {
            await SellerAccess.Require(db, actor, sellerId, SellerPermission.Finance);
            if (sellerId == Seller.FirstPartyId)
                throw new ShopRuleException("HKShop does not request seller payouts.");
            if (submissionKey == Guid.Empty || ShopRules.Vnd(amount) == 0)
                throw new ShopRuleException("Enter a valid payout request and a positive amount.");

            var existing = await db.First(db.Query<Payout>().Where(p => p.SellerId == sellerId && p.SubmissionKey == submissionKey));
            if (existing is not null)
            {
                if (existing.AmountVnd != amount) throw new ShopRuleException("This payout request has a different amount.");
                return existing.Id;
            }

            var available = await SettlementLedger.Balance(db, LedgerAccountType.SellerAvailable, sellerId);
            if (available < amount) throw new ShopRuleException("The available settlement balance is too low.");
            var now = time.GetUtcNow();
            var payout = new Payout
            {
                SellerId = sellerId,
                SubmissionKey = submissionKey,
                AmountVnd = amount,
                RequestedBy = actor.UserId,
                RequestedAt = now,
                Status = PayoutStatus.Requested
            };
            db.Add(payout);
            await PostMovement(db, payout, LedgerEventType.PayoutReserved, now,
                new LedgerPosting(LedgerAccountType.SellerAvailable, sellerId, -amount),
                new LedgerPosting(LedgerAccountType.SellerPayoutInTransit, sellerId, amount));
            db.Audit(actor.UserId, "Payout requested", payout.Id.ToString(), now, sellerId: sellerId);
            return payout.Id;
        });

    public Task<Page<PayoutQueueRow>> Queue(ShopActor actor, int page)
    {
        actor.RequireAdmin();
        return store.Read(db => ReadPage(db,
            from payout in db.Query<Payout>()
            join seller in db.Query<Seller>() on payout.SellerId equals seller.Id
            orderby payout.Status == PayoutStatus.Requested ? 0 : payout.Status == PayoutStatus.Processing ? 1 : 2,
                payout.RequestedAt descending, payout.Id descending
            select new PayoutQueueRow(payout, seller.DisplayName), page));
    }

    public Task<int> Review(ShopActor actor, Guid id, bool approve, string note)
    {
        actor.RequireAdmin();
        note = ShopRules.Text(note, 250, "Payout review note", required: !approve);
        return store.Write(async db =>
        {
            var payout = await db.First(db.Query<Payout>().Where(p => p.Id == id))
                ?? throw new ShopRuleException("Payout request not found.");
            if (payout.Status != PayoutStatus.Requested)
            {
                if ((approve && payout.ApprovedAt.HasValue) || (!approve && payout.Status == PayoutStatus.Rejected)) return 0;
                throw new ShopRuleException("This payout has already been reviewed.");
            }

            var now = time.GetUtcNow();
            if (approve)
            {
                if (await SettlementLedger.Balance(db, LedgerAccountType.SellerAvailable, payout.SellerId) < 0)
                    throw new ShopRuleException("The seller has a settlement deficit. Reject the payout or wait for funds.");
                payout.Status = PayoutStatus.Processing;
                payout.ApprovedAt = now;
                payout.NextAttemptAt = now;
            }
            else
            {
                await ReleaseReserve(db, payout, LedgerEventType.PayoutRejected, now);
                payout.Status = PayoutStatus.Rejected;
                payout.CompletedAt = now;
            }
            payout.ReviewNote = note;
            db.Audit(actor.UserId, approve ? "Payout approved" : "Payout rejected", payout.Id.ToString(),
                now, note, payout.SellerId);
            return 0;
        });
    }

    public Task<List<Guid>> DuePayouts() => store.Read(db =>
    {
        var now = time.GetUtcNow();
        return db.List(db.Query<Payout>().Where(p => p.Status == PayoutStatus.Processing && p.NextAttemptAt <= now)
            .OrderBy(p => p.NextAttemptAt).ThenBy(p => p.Id).Select(p => p.Id).Take(20));
    });

    public Task Reconcile(ShopActor actor, Guid id, CancellationToken cancellationToken)
    {
        actor.RequireAdmin();
        return Reconcile(id, cancellationToken);
    }

    public async Task Reconcile(Guid id, CancellationToken cancellationToken)
    {
        var job = await store.Write<PayoutJob?>(async db =>
        {
            var now = time.GetUtcNow();
            var payout = await db.First(db.Query<Payout>().Where(p => p.Id == id &&
                p.Status == PayoutStatus.Processing && p.NextAttemptAt <= now));
            if (payout is null) return null;
            payout.Attempts++;
            payout.NextAttemptAt = now.AddMinutes(2);
            return new PayoutJob(new PayoutGatewayRequest(payout.Id, payout.AmountVnd), payout.ProviderReference, payout.Attempts);
        });
        if (job is null) return;

        PayoutGatewayOutcome? outcome;
        try
        {
            outcome = job.Attempt > 1 ? await gateway.Query(job.Request.PayoutId, cancellationToken) : null;
            if (outcome is null && job.Reference.Length == 0)
                outcome = await gateway.Submit(job.Request, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is TimeoutException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            outcome = null;
        }

        await store.Write(async db =>
        {
            var payout = await db.First(db.Query<Payout>().Where(p => p.Id == id));
            if (payout is null || payout.Status != PayoutStatus.Processing || payout.Attempts != job.Attempt) return 0;
            var now = time.GetUtcNow();
            if (outcome is null)
            {
                payout.Issue = "Payout outcome unknown. The same payout request will be checked again.";
                payout.NextAttemptAt = now.AddMinutes(2);
                return 0;
            }
            if (outcome.AmountVnd != payout.AmountVnd || string.IsNullOrWhiteSpace(outcome.Reference) ||
                outcome.Reference.Length > 120 || (job.Reference.Length > 0 && outcome.Reference != job.Reference))
            {
                payout.Issue = "Payout provider returned a mismatched amount or reference.";
                payout.NextAttemptAt = now.AddMinutes(2);
                return 0;
            }
            var duplicate = await db.First(db.Query<Payout>().Where(p =>
                p.Id != id && p.ProviderReference == outcome.Reference).Select(p => (Guid?)p.Id));
            if (duplicate is not null)
            {
                payout.Issue = "The provider reference is already linked to another payout.";
                payout.NextAttemptAt = now.AddMinutes(2);
                return 0;
            }

            payout.ProviderReference = outcome.Reference;
            payout.Issue = outcome.Issue;
            if (outcome.Status is not (PayoutGatewayStatus.Succeeded or PayoutGatewayStatus.Failed))
            {
                payout.NextAttemptAt = now.AddSeconds(30);
                return 0;
            }

            if (outcome.Status == PayoutGatewayStatus.Succeeded)
            {
                await PostMovement(db, payout, LedgerEventType.PayoutSucceeded, now,
                    new LedgerPosting(LedgerAccountType.SellerPayoutInTransit, payout.SellerId, -payout.AmountVnd),
                    new LedgerPosting(LedgerAccountType.PayoutClearing, null, payout.AmountVnd));
                payout.Status = PayoutStatus.Paid;
            }
            else
            {
                await ReleaseReserve(db, payout, LedgerEventType.PayoutFailed, now);
                payout.Status = PayoutStatus.Failed;
            }
            payout.CompletedAt = now;
            payout.NextAttemptAt = null;
            db.Audit(null, payout.Status == PayoutStatus.Paid ? "Payout reconciled" : "Payout failed",
                payout.Id.ToString(), now, outcome.Reference, payout.SellerId);
            return 0;
        });
    }

    private static Task ReleaseReserve(IShopSession db, Payout payout, LedgerEventType type, DateTimeOffset now) =>
        PostMovement(db, payout, type, now,
            new LedgerPosting(LedgerAccountType.SellerPayoutInTransit, payout.SellerId, -payout.AmountVnd),
            new LedgerPosting(LedgerAccountType.SellerAvailable, payout.SellerId, payout.AmountVnd));

    private static Task PostMovement(IShopSession db, Payout payout, LedgerEventType type, DateTimeOffset now,
        LedgerPosting debit, LedgerPosting credit) =>
        SettlementLedger.Post(db, $"Payout:{payout.Id:N}:{type}", type, now, [debit, credit], payoutId: payout.Id);

    private static async Task<Page<T>> ReadPage<T>(IShopSession db, IQueryable<T> query, int page)
    {
        var count = await db.Count(query);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)PageSize)));
        var rows = await db.List(query.Skip((number - 1) * PageSize).Take(PageSize));
        return new Page<T>(rows, number, count, PageSize);
    }

    private sealed record PayoutJob(PayoutGatewayRequest Request, string Reference, int Attempt);
}
