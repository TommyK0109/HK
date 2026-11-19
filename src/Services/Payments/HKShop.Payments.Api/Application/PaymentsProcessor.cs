using System.Globalization;
using HKShop.Application;
using HKShop.Application.Payments;
using HKShop.Application.Settlement;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;
using HKShop.Messaging;
using HKShop.Payments.Api.Domain;
using HKShop.Payments.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Payments.Api.Application;

public sealed record LedgerPosting(LedgerAccountType Account, long? SellerId, decimal Vnd);

public sealed class PaymentsProcessor(
    IDbContextFactory<PaymentsDbContext> factory,
    SePayPaymentGateway sePay,
    SePayRefundGatewayService refundGateway,
    SimulatedPayoutGatewayService payoutGateway,
    ShopSettings shopSettings,
    TimeProvider time)
{
    private const string VoidIssue = "SePay reported a cancelled or voided transaction. Verify before processing.";
    private const long FirstPartySellerId = 1;

    public bool OnlineAvailable => sePay.Available;

    public async Task<OrderPaymentDetailsDto?> GetOrderPaymentDetailsAsync(ShopActor actor, Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.OrderProjections.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId && (actor.Staff || actor.Admin || o.CustomerId == actor.UserId), ct);
        if (order is null) return null;

        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, ct);
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId)
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new EOfficePaymentAttemptDto(
                a.Id, a.OrderId, a.SellerOrderId, a.AttemptNumber, a.Method, a.Status,
                a.AmountVnd, a.CapturedAmountVnd, a.ProviderInvoice, a.ProviderTransaction, a.AppliedToOrder, a.Issue))
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new EOfficeRefundDto(
                r.Id, r.OrderId, r.PaymentAttemptId, r.SellerOrderId, r.ReturnId, r.BusinessKey,
                r.AmountVnd, r.ShippingVnd, r.Status, r.Simulated, r.Reference, r.ProviderReference,
                r.Issue, r.NextAttemptAt, r.RequestedAt, r.CompletedAt))
            .ToListAsync(ct);

        return new OrderPaymentDetailsDto(
            payment is null ? null : new EOfficePaymentDto(payment.OrderId, payment.Method, payment.Status, payment.AmountVnd, payment.Issue),
            attempts,
            refunds);
    }

    public async Task<HostedCheckout> BuildCheckoutFormAsync(ShopActor actor, Guid orderId, CancellationToken ct)
    {
        actor.RequireCustomer();
        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.OrderProjections.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerId == actor.UserId, ct)
            ?? throw new ShopRuleException("Order not found.");
        if (order.Status != OrderStatus.AwaitingPayment)
            throw new ShopRuleException("The current order status does not allow this action.");
        if (order.PaymentDeadline <= time.GetUtcNow())
            throw new ShopRuleException("Payment session has expired.");

        var attempt = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync(ct);
        if (attempt is null || attempt.Method != PaymentMethod.SePay || attempt.Status != PaymentStatus.Pending ||
            attempt.ProviderInvoice is null || attempt.AppliedToOrder || attempt.AmountVnd <= 0)
            throw new ShopRuleException("This order cannot be paid via SePay right now.");

        return sePay.BuildCheckout(attempt.ProviderInvoice, order.OrderId, attempt.AmountVnd);
    }

    public async Task<string> CreateRetryAttemptAsync(ShopActor actor, Guid orderId, CancellationToken ct)
    {
        actor.RequireCustomer();
        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerId == actor.UserId, ct)
            ?? throw new ShopRuleException("Order not found.");
        if (order.Status != OrderStatus.AwaitingPayment)
            throw new ShopRuleException("The current order status does not allow this action.");
        var now = time.GetUtcNow();
        if (order.PaymentDeadline <= now)
            throw new ShopRuleException("Payment session has expired.");

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct)
            ?? throw new ShopRuleException("Payment record not found.");
        if (payment.Method != PaymentMethod.SePay || order.TotalVnd <= 0)
            throw new ShopRuleException("This order does not support SePay retry.");

        var attempts = await db.PaymentAttempts.Where(a => a.OrderId == orderId).OrderBy(a => a.AttemptNumber).ToListAsync(ct);
        if (attempts.Any(a => a.AppliedToOrder))
            throw new ShopRuleException("Payment for this order has already been applied.");

        foreach (var pending in attempts.Where(a => a.Status == PaymentStatus.Pending))
        {
            pending.Status = PaymentStatus.Cancelled;
            pending.NextQueryAt = null;
        }

        var nextNumber = (attempts.Count == 0 ? 0 : attempts[^1].AttemptNumber) + 1;
        var retry = new PaymentAttempt
        {
            OrderId = order.OrderId,
            AttemptNumber = nextNumber,
            Method = PaymentMethod.SePay,
            Status = PaymentStatus.Pending,
            AmountVnd = order.TotalVnd,
            CreatedAt = now,
            NextQueryAt = now.AddMinutes(2)
        };
        retry.ProviderInvoice = $"HP{retry.Id:N}".ToUpperInvariant();
        db.PaymentAttempts.Add(retry);
        payment.Status = PaymentStatus.Pending;
        payment.NextQueryAt = retry.NextQueryAt;
        await db.SaveChangesAsync(ct);
        return retry.ProviderInvoice;
    }

    public async Task<bool> ApplyGatewayOutcomeAsync(GatewayOutcome outcome, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var attempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.ProviderInvoice == outcome.Invoice, ct);
        if (attempt is null)
        {
            var proj = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderNumber == outcome.Invoice, ct);
            if (proj is null) return false;
            attempt = await db.PaymentAttempts.Where(a => a.OrderId == proj.OrderId).OrderBy(a => a.AttemptNumber).FirstOrDefaultAsync(ct);
            if (attempt is null) return false;
        }

        var order = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == attempt.OrderId, ct)
            ?? throw new ShopRuleException("Order not found.");
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == order.OrderId, ct)
            ?? throw new ShopRuleException("Payment record not found.");
        if (attempt.Method != PaymentMethod.SePay || payment.Method != PaymentMethod.SePay) return false;

        var now = time.GetUtcNow();
        var eventKey = outcome.Paid
            ? $"SePay:{outcome.Transaction}:paid"
            : (string.IsNullOrEmpty(outcome.Transaction)
                ? $"SePay:{attempt.ProviderInvoice}:failed"
                : $"SePay:{outcome.Transaction}:failed");

        var existingEvent = await db.PaymentEvents.FirstOrDefaultAsync(e => e.EventKey == eventKey, ct);
        if (existingEvent is not null) return existingEvent.OrderId == order.OrderId;

        if (!string.IsNullOrEmpty(outcome.Transaction))
        {
            var txAttempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.ProviderTransaction == outcome.Transaction, ct);
            if (txAttempt is not null) return txAttempt.OrderId == order.OrderId;
        }

        if (outcome.Currency != "VND")
        {
            attempt.Issue = "Payment currency mismatch.";
            payment.Issue = "Payment currency mismatch.";
            await db.SaveChangesAsync(ct);
            return false;
        }

        var attempts = await db.PaymentAttempts.Where(a => a.OrderId == order.OrderId).OrderBy(a => a.AttemptNumber).ToListAsync(ct);
        var sellerOrders = await db.SellerOrderProjections.Where(s => s.OrderId == order.OrderId).OrderBy(s => s.SellerId).ToListAsync(ct);

        if (!outcome.Paid)
        {
            if (attempt.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            {
                attempt.Status = PaymentStatus.Failed;
                attempt.NextQueryAt = null;
            }
            db.PaymentEvents.Add(new PaymentEvent
            {
                OrderId = order.OrderId,
                PaymentAttemptId = attempt.Id,
                EventType = "Failed",
                EventKey = eventKey,
                ReceivedAt = now
            });

            var hasOtherOpenOrPaid = attempts.Any(a => a.Id != attempt.Id && (a.AppliedToOrder || a.Status == PaymentStatus.Pending));
            if (order.Status == OrderStatus.AwaitingPayment && !hasOtherOpenOrPaid)
            {
                order.Status = OrderStatus.PaymentFailed;
                payment.Status = PaymentStatus.Failed;
                payment.NextQueryAt = null;
                db.EnqueueOutboxEvent("PaymentAttempt", attempt.Id.ToString("D"), nameof(PaymentFailed),
                    new PaymentFailed(attempt.Id, order.OrderId, order.OrderNumber, order.CustomerEmail, "Gateway reported failure.", now),
                    $"PaymentFailed:{attempt.Id:N}", now);
            }
            await db.SaveChangesAsync(ct);
            return true;
        }

        var targetAttempt = attempt.ProviderTransaction is null && attempt.Status != PaymentStatus.Paid
            ? attempt
            : CreateExtraCaptureAttempt(db, order.OrderId, attempts, attempt.AmountVnd, now);

        targetAttempt.Status = PaymentStatus.Paid;
        targetAttempt.CapturedAmountVnd = outcome.Amount;
        targetAttempt.ProviderTransaction = outcome.Transaction;
        targetAttempt.PaidAt = now;
        targetAttempt.NextQueryAt = null;
        await RecordCaptureAsync(db, targetAttempt, now, ct);

        var appliedAttempt = attempts.FirstOrDefault(a => a.AppliedToOrder);
        if (appliedAttempt is not null)
        {
            targetAttempt.AppliedToOrder = false;
            targetAttempt.Issue = $"Excess capture ({outcome.Amount:N0} VND) received for an already paid order.";
            payment.Issue = "Excess capture recorded for reconciliation.";
            db.PaymentEvents.Add(new PaymentEvent
            {
                OrderId = order.OrderId,
                PaymentAttemptId = targetAttempt.Id,
                EventType = "ExcessCapture",
                EventKey = eventKey,
                ReceivedAt = now
            });
            QueueCaptureRefund(db, targetAttempt, "excess", now);
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (outcome.Amount != attempt.AmountVnd)
        {
            var isOverpayment = outcome.Amount > attempt.AmountVnd;
            targetAttempt.AppliedToOrder = false;
            targetAttempt.Issue = isOverpayment
                ? $"Excess capture amount ({outcome.Amount:N0} VND vs expected {attempt.AmountVnd:N0} VND)."
                : $"Underpayment captured ({outcome.Amount:N0} VND vs expected {attempt.AmountVnd:N0} VND).";
            payment.Issue = targetAttempt.Issue;
            payment.NextQueryAt = null;
            db.PaymentEvents.Add(new PaymentEvent
            {
                OrderId = order.OrderId,
                PaymentAttemptId = targetAttempt.Id,
                EventType = isOverpayment ? "ExcessCapture" : "AmountMismatch",
                EventKey = eventKey,
                ReceivedAt = now
            });
            QueueCaptureRefund(db, targetAttempt, "mismatch", now);
            if (order.Status == OrderStatus.AwaitingPayment && order.PaymentDeadline <= now)
                ExpireOrderInternal(db, order, payment, attempts, now);
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (order.Status == OrderStatus.AwaitingPayment && order.PaymentDeadline > now)
        {
            targetAttempt.AppliedToOrder = true;
            targetAttempt.Issue = "";
            foreach (var other in attempts.Where(a => a.Id != targetAttempt.Id && a.Status == PaymentStatus.Pending))
            {
                other.Status = PaymentStatus.Cancelled;
                other.NextQueryAt = null;
            }

            payment.Status = PaymentStatus.Paid;
            payment.ProviderTransaction = outcome.Transaction;
            payment.PaidAt = now;
            payment.NextQueryAt = null;
            payment.Issue = "";
            order.Status = OrderStatus.Confirmed;

            await RecordSalesAsync(db, targetAttempt, sellerOrders, now, ct);
            db.PaymentEvents.Add(new PaymentEvent
            {
                OrderId = order.OrderId,
                PaymentAttemptId = targetAttempt.Id,
                EventType = "Captured",
                EventKey = eventKey,
                ReceivedAt = now
            });
            db.EnqueueOutboxEvent("PaymentAttempt", targetAttempt.Id.ToString("D"), nameof(PaymentCaptured),
                new PaymentCaptured(targetAttempt.Id, order.OrderId, order.OrderNumber, targetAttempt.CapturedAmountVnd,
                    targetAttempt.ProviderTransaction, now),
                $"PaymentCaptured:{targetAttempt.Id:N}", now);

            await db.SaveChangesAsync(ct);
            return true;
        }

        if (order.Status == OrderStatus.AwaitingPayment)
            ExpireOrderInternal(db, order, payment, attempts, now);

        targetAttempt.AppliedToOrder = false;
        targetAttempt.Issue = $"Late capture received while order status was {order.Status}.";
        payment.Issue = "Late capture recorded after order expired or closed; refund queued for reconciliation.";
        payment.NextQueryAt = null;
        db.PaymentEvents.Add(new PaymentEvent
        {
            OrderId = order.OrderId,
            PaymentAttemptId = targetAttempt.Id,
            EventType = "LateCapture",
            EventKey = eventKey,
            ReceivedAt = now
        });
        QueueCaptureRefund(db, targetAttempt, "late", now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task FlagVoidAsync(string invoice, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var attempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.ProviderInvoice == invoice, ct);
        Guid? orderId = attempt?.OrderId;
        if (orderId is null)
        {
            var proj = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderNumber == invoice, ct);
            if (proj is null) return;
            orderId = proj.OrderId;
            attempt = await db.PaymentAttempts.Where(a => a.OrderId == proj.OrderId)
                .OrderByDescending(a => a.AttemptNumber).FirstOrDefaultAsync(ct);
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId.Value, ct);
        if (payment is { Method: PaymentMethod.SePay })
        {
            payment.Issue = VoidIssue;
            if (attempt is not null) attempt.Issue = VoidIssue;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> ExpireDuePaymentsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var due = await db.OrderProjections
            .Where(o => o.Status == OrderStatus.AwaitingPayment && o.PaymentDeadline <= now)
            .OrderBy(o => o.PaymentDeadline)
            .Take(25)
            .ToListAsync(ct);

        foreach (var order in due)
        {
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == order.OrderId, ct);
            var attempts = await db.PaymentAttempts.Where(a => a.OrderId == order.OrderId).ToListAsync(ct);
            if (payment is not null)
                ExpireOrderInternal(db, order, payment, attempts, now);
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    public async Task<List<PaymentJobDto>> DueQueriesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return await (from attempt in db.PaymentAttempts.AsNoTracking()
                      where attempt.Method == PaymentMethod.SePay && attempt.ProviderInvoice != null && attempt.NextQueryAt <= now
                      orderby attempt.NextQueryAt
                      select new PaymentJobDto(attempt.OrderId, attempt.ProviderInvoice!))
            .Take(20)
            .ToListAsync(ct);
    }

    public async Task ReconcilePaymentAsync(Guid orderId, string invoice, CancellationToken ct)
    {
        GatewayOutcome? outcome = null;
        Exception? failure = null;
        try { outcome = await sePay.Query(invoice, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            failure = error;
        }

        if (outcome is not null && await ApplyGatewayOutcomeAsync(outcome, ct) && outcome.Paid)
        {
            await using var clearDb = await factory.CreateDbContextAsync(ct);
            var capture = await clearDb.PaymentAttempts.FirstOrDefaultAsync(a =>
                a.OrderId == orderId && a.Status == PaymentStatus.Paid && a.AppliedToOrder &&
                a.Issue == VoidIssue && a.ProviderTransaction == outcome.Transaction &&
                a.CapturedAmountVnd == outcome.Amount && outcome.Currency == "VND", ct);
            if (capture is not null)
            {
                capture.Issue = "";
                var pay = await clearDb.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);
                if (pay is not null && pay.Issue == VoidIssue) pay.Issue = "";
                await clearDb.SaveChangesAsync(ct);
            }
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);
        var attempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.OrderId == orderId && a.ProviderInvoice == invoice, ct);
        if (order is null || payment is null || (payment.Status == PaymentStatus.Paid && (attempt is null || attempt.Status == PaymentStatus.Paid)))
            return;

        var now = time.GetUtcNow();
        var age = now - order.CreatedAt;
        if (age >= TimeSpan.FromHours(24))
        {
            if (attempt is not null)
            {
                attempt.NextQueryAt = null;
                if (attempt.Status == PaymentStatus.Pending || outcome?.Paid == true || failure is not null)
                    attempt.Issue = "Verify transaction status on SePay.";
            }
            payment.NextQueryAt = null;
            if (payment.Status == PaymentStatus.Pending || outcome?.Paid == true || failure is not null)
                payment.Issue = "Verify transaction status on SePay.";
        }
        else
        {
            var next = now + (age < TimeSpan.FromMinutes(20) ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
            if (attempt is not null && attempt.Status != PaymentStatus.Paid) attempt.NextQueryAt = next;
            payment.NextQueryAt = next;
        }
        await db.SaveChangesAsync(ct);

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async Task<List<long>> DueRefundsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return await db.Refunds.AsNoTracking()
            .Where(r => r.Status == RefundStatus.Pending && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Id)
            .Select(r => r.Id)
            .Take(20)
            .ToListAsync(ct);
    }

    public async Task ReconcileRefundAsync(long refundId, CancellationToken ct)
    {
        RefundGatewayRequest? req = null;
        var reference = "";
        var attemptNo = 0;

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var now = time.GetUtcNow();
            var row = await db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId && r.Status == RefundStatus.Pending && r.NextAttemptAt <= now, ct);
            if (row is null) return;
            var capture = await db.PaymentAttempts.FirstOrDefaultAsync(a =>
                a.Id == row.PaymentAttemptId && a.OrderId == row.OrderId && a.Status == PaymentStatus.Paid, ct);
            if (capture is null)
            {
                row.NextAttemptAt = null;
                row.Issue = "Verify the original capture and reconcile this refund manually.";
                await db.SaveChangesAsync(ct);
                return;
            }
            row.Attempts++;
            row.NextAttemptAt = now.AddMinutes(2);
            req = new RefundGatewayRequest(row.BusinessKey, capture.ProviderTransaction, row.AmountVnd);
            reference = row.ProviderReference;
            attemptNo = row.Attempts;
            await db.SaveChangesAsync(ct);
        }

        RefundGatewayOutcome outcome;
        try
        {
            outcome = reference.Length == 0
                ? refundGateway.Submit(req, ct)
                : refundGateway.Query(req, reference, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            outcome = new RefundGatewayOutcome(RefundGatewayStatus.Pending, reference, req.AmountVnd,
                "Provider outcome unknown. The refund will be checked again with the same request key.");
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.Refunds.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == refundId, ct);
            if (row is null || row.Status != RefundStatus.Pending || row.Attempts != attemptNo) return;
            if (outcome.AmountVnd != row.AmountVnd || outcome.Reference.Length > 100 ||
                (reference.Length > 0 && outcome.Reference != reference) ||
                (outcome.Status == RefundGatewayStatus.Succeeded && outcome.Reference.Length == 0))
            {
                row.NextAttemptAt = null;
                row.Issue = "Provider returned a mismatched amount or reference. Reconcile this refund manually.";
                await db.SaveChangesAsync(ct);
                return;
            }
            row.ProviderReference = outcome.Reference;
            row.Issue = outcome.Issue;
            if (outcome.Status == RefundGatewayStatus.NeedsReview) row.NextAttemptAt = null;
            if (outcome.Status == RefundGatewayStatus.Succeeded)
                await MarkRefundCompletedAsync(db, row, outcome.Reference, time.GetUtcNow(), outcome.Simulated, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task CompleteRefundAsync(ShopActor actor, long refundId, string reference, CancellationToken ct)
    {
        actor.RequireAdmin();
        reference = ShopRules.Text(reference, 100, "Confirmed refund reference");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Refunds.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == refundId, ct)
            ?? throw new ShopRuleException("Refund request not found.");
        if (row.Status == RefundStatus.Completed) return;
        if (row.NextAttemptAt is not null)
            throw new ShopRuleException("This refund is still scheduled for automatic reconciliation.");
        if (row.ProviderReference.Length > 0 && row.ProviderReference != reference)
            throw new ShopRuleException("The confirmed reference must match the provider refund reference.");

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == row.OrderId, ct)
            ?? throw new ShopRuleException("Payment record not found.");
        var capture = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.OrderId == row.OrderId &&
            (row.PaymentAttemptId == null ? a.AppliedToOrder : a.Id == row.PaymentAttemptId), ct);
        var captured = capture is { Status: PaymentStatus.Paid } ? capture.CapturedAmountVnd :
            capture is null && row.PaymentAttemptId is null && payment.Status == PaymentStatus.Paid ? payment.AmountVnd : 0;
        var attemptId = capture?.Id;
        var includeLegacy = capture is null || capture.AppliedToOrder;
        var reserved = await db.Refunds.Where(r => r.OrderId == row.OrderId &&
            (r.PaymentAttemptId == attemptId || (r.PaymentAttemptId == null && includeLegacy))).SumAsync(r => r.AmountVnd, ct);
        if (captured == 0 || reserved > captured)
            throw new ShopRuleException("Reconcile the captured payment before confirming this refund.");

        var now = time.GetUtcNow();
        var simulated = payment.Method == PaymentMethod.Cod;
        await MarkRefundCompletedAsync(db, row, reference, now, simulated, ct);
        var sellerOrder = row.SellerOrderId is null
            ? null
            : await db.SellerOrderProjections.FirstOrDefaultAsync(s => s.SellerOrderId == row.SellerOrderId, ct);
        db.AddAuditEntry(actor.UserId, simulated ? "Hoàn tiền mô phỏng" : "Xác nhận đối soát hoàn tiền",
            row.Id.ToString(CultureInfo.InvariantCulture), now, reference, sellerOrder?.SellerId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ReleaseDueSettlementsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-Math.Clamp(shopSettings.SettlementHoldDays, 0, 3650));

        var pendingEntries = from entry in db.LedgerEntries
                             join account in db.LedgerAccounts on entry.AccountId equals account.Id
                             join journal in db.LedgerJournals on entry.JournalId equals journal.Id
                             where account.Type == LedgerAccountType.SellerPending && journal.SellerOrderId != null
                             select new { journal.SellerOrderId, Vnd = entry.Side == LedgerSide.Credit ? entry.AmountVnd : -entry.AmountVnd };

        var due = await (from sale in db.LedgerJournals
                         join order in db.SellerOrderProjections on sale.SellerOrderId equals (Guid?)order.SellerOrderId
                         join capture in db.PaymentAttempts on sale.PaymentAttemptId equals (Guid?)capture.Id
                         where sale.Type == LedgerEventType.Sale && order.SellerId != FirstPartySellerId
                             && order.Status == OrderStatus.Delivered && order.DeliveredAt <= cutoff
                             && capture.Status == PaymentStatus.Paid && capture.AppliedToOrder && capture.PaidAt <= cutoff
                             && capture.Issue == ""
                             && !db.LedgerJournals.Any(j => j.SellerOrderId == order.SellerOrderId && j.Type == LedgerEventType.SettlementRelease)
                             && !db.Refunds.Any(r => r.SellerOrderId == order.SellerOrderId && r.Status == RefundStatus.Pending)
                         let amount = pendingEntries.Where(e => e.SellerOrderId == order.SellerOrderId).Sum(e => e.Vnd)
                         where amount > 0
                         orderby order.DeliveredAt, order.SellerOrderId
                         select new { order.SellerOrderId, order.SellerId, CaptureId = capture.Id, Amount = amount })
            .Take(25)
            .ToListAsync(ct);

        foreach (var item in due)
        {
            await PostJournalAsync(db, $"release:{item.SellerOrderId:N}", LedgerEventType.SettlementRelease, now,
                [new(LedgerAccountType.SellerPending, item.SellerId, -item.Amount),
                 new(LedgerAccountType.SellerAvailable, item.SellerId, item.Amount)],
                ct, sellerOrderId: item.SellerOrderId, paymentAttemptId: item.CaptureId);
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    public async Task<SellerLedgerBalanceDto> GetSellerBalancesAsync(ShopActor actor, long sellerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFinanceAccessAsync(db, actor, sellerId, ct);
        return new SellerLedgerBalanceDto(
            await BalanceAsync(db, LedgerAccountType.SellerPending, sellerId, ct),
            await BalanceAsync(db, LedgerAccountType.SellerAvailable, sellerId, ct),
            await BalanceAsync(db, LedgerAccountType.SellerPayoutInTransit, sellerId, ct));
    }

    public async Task<Guid> RequestPayoutAsync(ShopActor actor, long sellerId, Guid submissionKey, decimal amount, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFinanceAccessAsync(db, actor, sellerId, ct);
        if (sellerId == FirstPartySellerId)
            throw new ShopRuleException("HKShop does not request seller payouts.");
        if (submissionKey == Guid.Empty || ShopRules.Vnd(amount) == 0)
            throw new ShopRuleException("Enter a valid payout request and a positive amount.");

        var existing = await db.Payouts.FirstOrDefaultAsync(p => p.SellerId == sellerId && p.SubmissionKey == submissionKey, ct);
        if (existing is not null)
        {
            if (existing.AmountVnd != amount) throw new ShopRuleException("This payout request has a different amount.");
            return existing.Id;
        }

        var available = await BalanceAsync(db, LedgerAccountType.SellerAvailable, sellerId, ct);
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
        db.Payouts.Add(payout);
        await PostJournalAsync(db, $"Payout:{payout.Id:N}:{LedgerEventType.PayoutReserved}", LedgerEventType.PayoutReserved, now,
            [new(LedgerAccountType.SellerAvailable, sellerId, -amount),
             new(LedgerAccountType.SellerPayoutInTransit, sellerId, amount)],
            ct, payoutId: payout.Id);
        db.AddAuditEntry(actor.UserId, "Payout requested", payout.Id.ToString(), now, sellerId: sellerId);
        await db.SaveChangesAsync(ct);
        return payout.Id;
    }

    public async Task<Page<PayoutQueueItemDto>> GetPayoutQueueAsync(ShopActor actor, int page, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        const int size = 25;
        var query = from payout in db.Payouts.AsNoTracking()
                    join seller in db.SellerProjections.AsNoTracking() on payout.SellerId equals seller.SellerId into sellers
                    from seller in sellers.DefaultIfEmpty()
                    orderby payout.Status == PayoutStatus.Requested ? 0 : payout.Status == PayoutStatus.Processing ? 1 : 2,
                        payout.RequestedAt descending, payout.Id descending
                    select new PayoutQueueItemDto(
                        payout.Id, payout.SellerId, seller != null ? seller.DisplayName : $"Seller #{payout.SellerId}",
                        payout.AmountVnd, payout.Status, payout.ProviderReference, payout.Issue, payout.ReviewNote,
                        payout.RequestedAt, payout.NextAttemptAt, payout.CompletedAt);

        var count = await query.CountAsync(ct);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
        return new Page<PayoutQueueItemDto>(await query.Skip((number - 1) * size).Take(size).ToListAsync(ct), number, count, size);
    }

    public async Task ReviewPayoutAsync(ShopActor actor, Guid payoutId, bool approve, string note, CancellationToken ct)
    {
        actor.RequireAdmin();
        note = ShopRules.Text(note, 250, "Payout review note", required: !approve);
        await using var db = await factory.CreateDbContextAsync(ct);
        var payout = await db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, ct)
            ?? throw new ShopRuleException("Payout request not found.");
        if (payout.Status != PayoutStatus.Requested)
        {
            if ((approve && payout.ApprovedAt.HasValue) || (!approve && payout.Status == PayoutStatus.Rejected)) return;
            throw new ShopRuleException("This payout has already been reviewed.");
        }

        var now = time.GetUtcNow();
        if (approve)
        {
            if (await BalanceAsync(db, LedgerAccountType.SellerAvailable, payout.SellerId, ct) < 0)
                throw new ShopRuleException("The seller has a settlement deficit. Reject the payout or wait for funds.");
            payout.Status = PayoutStatus.Processing;
            payout.ApprovedAt = now;
            payout.NextAttemptAt = now;
            db.EnqueueOutboxEvent("Payout", payout.Id.ToString("D"), nameof(PayoutApproved),
                new PayoutApproved(payout.Id, payout.SellerId, payout.AmountVnd, now),
                $"PayoutApproved:{payout.Id:N}", now);
        }
        else
        {
            await PostJournalAsync(db, $"Payout:{payout.Id:N}:{LedgerEventType.PayoutRejected}", LedgerEventType.PayoutRejected, now,
                [new(LedgerAccountType.SellerPayoutInTransit, payout.SellerId, -payout.AmountVnd),
                 new(LedgerAccountType.SellerAvailable, payout.SellerId, payout.AmountVnd)],
                ct, payoutId: payout.Id);
            payout.Status = PayoutStatus.Rejected;
            payout.CompletedAt = now;
        }
        payout.ReviewNote = note;
        db.AddAuditEntry(actor.UserId, approve ? "Payout approved" : "Payout rejected", payout.Id.ToString(), now, note, payout.SellerId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Guid>> DuePayoutsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return await db.Payouts.AsNoTracking()
            .Where(p => p.Status == PayoutStatus.Processing && p.NextAttemptAt <= now)
            .OrderBy(p => p.NextAttemptAt).ThenBy(p => p.Id)
            .Select(p => p.Id)
            .Take(20)
            .ToListAsync(ct);
    }

    public async Task ReconcilePayoutAsync(Guid payoutId, CancellationToken ct)
    {
        PayoutGatewayRequest? request = null;
        var reference = "";
        var attemptNo = 0;

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var now = time.GetUtcNow();
            var payout = await db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId && p.Status == PayoutStatus.Processing && p.NextAttemptAt <= now, ct);
            if (payout is null) return;
            payout.Attempts++;
            payout.NextAttemptAt = now.AddMinutes(2);
            request = new PayoutGatewayRequest(payout.Id, payout.AmountVnd);
            reference = payout.ProviderReference;
            attemptNo = payout.Attempts;
            await db.SaveChangesAsync(ct);
        }

        PayoutGatewayOutcome? outcome;
        try
        {
            outcome = attemptNo > 1 ? await payoutGateway.QueryAsync(request.PayoutId, ct) : null;
            if (outcome is null && reference.Length == 0)
                outcome = await payoutGateway.SubmitAsync(request, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is TimeoutException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            outcome = null;
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var payout = await db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, ct);
            if (payout is null || payout.Status != PayoutStatus.Processing || payout.Attempts != attemptNo) return;
            var now = time.GetUtcNow();
            if (outcome is null)
            {
                payout.Issue = "Payout outcome unknown. The same payout request will be checked again.";
                payout.NextAttemptAt = now.AddMinutes(2);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (outcome.AmountVnd != payout.AmountVnd || string.IsNullOrWhiteSpace(outcome.Reference) ||
                outcome.Reference.Length > 120 || (reference.Length > 0 && outcome.Reference != reference))
            {
                payout.Issue = "Payout provider returned a mismatched amount or reference.";
                payout.NextAttemptAt = now.AddMinutes(2);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (await db.Payouts.AnyAsync(p => p.Id != payoutId && p.ProviderReference == outcome.Reference, ct))
            {
                payout.Issue = "The provider reference is already linked to another payout.";
                payout.NextAttemptAt = now.AddMinutes(2);
                await db.SaveChangesAsync(ct);
                return;
            }

            payout.ProviderReference = outcome.Reference;
            payout.Issue = outcome.Issue;
            if (outcome.Status is not (PayoutGatewayStatus.Succeeded or PayoutGatewayStatus.Failed))
            {
                payout.NextAttemptAt = now.AddSeconds(30);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (outcome.Status == PayoutGatewayStatus.Succeeded)
            {
                await PostJournalAsync(db, $"Payout:{payout.Id:N}:{LedgerEventType.PayoutSucceeded}", LedgerEventType.PayoutSucceeded, now,
                    [new(LedgerAccountType.SellerPayoutInTransit, payout.SellerId, -payout.AmountVnd),
                     new(LedgerAccountType.PayoutClearing, null, payout.AmountVnd)],
                    ct, payoutId: payout.Id);
                payout.Status = PayoutStatus.Paid;
                db.EnqueueOutboxEvent("Payout", payout.Id.ToString("D"), nameof(PayoutPaid),
                    new PayoutPaid(payout.Id, payout.SellerId, payout.AmountVnd, outcome.Reference, now),
                    $"PayoutPaid:{payout.Id:N}", now);
            }
            else
            {
                await PostJournalAsync(db, $"Payout:{payout.Id:N}:{LedgerEventType.PayoutFailed}", LedgerEventType.PayoutFailed, now,
                    [new(LedgerAccountType.SellerPayoutInTransit, payout.SellerId, -payout.AmountVnd),
                     new(LedgerAccountType.SellerAvailable, payout.SellerId, payout.AmountVnd)],
                    ct, payoutId: payout.Id);
                payout.Status = PayoutStatus.Failed;
            }
            payout.CompletedAt = now;
            payout.NextAttemptAt = null;
            db.AddAuditEntry(null, payout.Status == PayoutStatus.Paid ? "Payout reconciled" : "Payout failed",
                payout.Id.ToString(), now, outcome.Reference, payout.SellerId);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<PaymentIssue>> IssuesAsync(ShopActor actor, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from attempt in db.PaymentAttempts.AsNoTracking()
                      join order in db.OrderProjections.AsNoTracking() on attempt.OrderId equals order.OrderId
                      join payment in db.Payments.AsNoTracking() on order.OrderId equals payment.OrderId
                      where attempt.Method == PaymentMethod.SePay &&
                            (attempt.Issue != "" || attempt.Status == PaymentStatus.Pending || payment.Issue != "")
                      orderby order.CreatedAt descending, attempt.AttemptNumber descending
                      select new PaymentIssue(
                          order.OrderId, order.OrderNumber, attempt.ProviderInvoice ?? order.OrderNumber,
                          attempt.Status, attempt.AmountVnd, attempt.CapturedAmountVnd,
                          attempt.Issue != "" ? attempt.Issue : payment.Issue, attempt.CreatedAt))
            .Take(100)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EOfficeRefundDto>> PendingRefundsAsync(ShopActor actor, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Refunds.AsNoTracking()
            .Where(r => r.Status == RefundStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .Take(100)
            .Select(r => new EOfficeRefundDto(
                r.Id, r.OrderId, r.PaymentAttemptId, r.SellerOrderId, r.ReturnId, r.BusinessKey,
                r.AmountVnd, r.ShippingVnd, r.Status, r.Simulated, r.Reference, r.ProviderReference,
                r.Issue, r.NextAttemptAt, r.RequestedAt, r.CompletedAt))
            .ToListAsync(ct);
    }

    // Internal ledger helpers also used by PaymentsInboxHandler
    public static async Task PostJournalAsync(
        PaymentsDbContext db,
        string key,
        LedgerEventType type,
        DateTimeOffset now,
        IReadOnlyList<LedgerPosting> entries,
        CancellationToken ct,
        Guid? sellerOrderId = null,
        Guid? paymentAttemptId = null,
        long? refundId = null,
        Guid? payoutId = null)
    {
        var postings = entries.GroupBy(e => (e.Account, e.SellerId))
            .Select(g => new LedgerPosting(g.Key.Account, g.Key.SellerId, g.Sum(x => x.Vnd)))
            .Where(e => e.Vnd != 0)
            .OrderBy(e => e.Account).ThenBy(e => e.SellerId)
            .ToList();
        if (postings.Count == 0 || await db.LedgerJournals.AnyAsync(j => j.BusinessKey == key, ct))
            return;
        if (postings.Sum(e => e.Vnd) != 0)
            throw new ShopRuleException("Settlement journal must balance in whole VND.");

        var accounts = new List<LedgerAccount>(postings.Count);
        var created = false;
        foreach (var posting in postings)
        {
            var account = await db.LedgerAccounts.FirstOrDefaultAsync(a => a.Type == posting.Account && a.SellerId == posting.SellerId, ct);
            if (account is null)
            {
                account = new LedgerAccount { Type = posting.Account, SellerId = posting.SellerId };
                db.LedgerAccounts.Add(account);
                created = true;
            }
            accounts.Add(account);
        }
        if (created) await db.SaveChangesAsync(ct);

        db.LedgerJournals.Add(new LedgerJournal
        {
            BusinessKey = key,
            Type = type,
            PostedAt = now,
            SellerOrderId = sellerOrderId,
            PaymentAttemptId = paymentAttemptId,
            RefundId = refundId,
            PayoutId = payoutId,
            Entries = postings.Select((p, i) => new LedgerEntry
            {
                AccountId = accounts[i].Id,
                AmountVnd = Math.Abs(p.Vnd),
                Side = p.Vnd > 0 ? LedgerSide.Credit : LedgerSide.Debit
            }).ToList()
        });
    }

    public static Task RecordCaptureAsync(PaymentsDbContext db, PaymentAttempt capture, DateTimeOffset now, CancellationToken ct) =>
        PostJournalAsync(db, $"capture:{capture.Id:N}", LedgerEventType.Capture, now,
            [new(LedgerAccountType.PaymentClearing, null, -capture.CapturedAmountVnd),
             new(LedgerAccountType.MarketplaceReceipts, null, capture.CapturedAmountVnd)],
            ct, paymentAttemptId: capture.Id);

    public static async Task RecordSalesAsync(
        PaymentsDbContext db,
        PaymentAttempt capture,
        IEnumerable<PaymentSellerOrderProjection> sellerOrders,
        DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var order in sellerOrders.OrderBy(o => o.SellerId))
        {
            var (account, sellerId) = order.SellerId == FirstPartySellerId
                ? (LedgerAccountType.PlatformMerchandise, (long?)null)
                : (LedgerAccountType.SellerPending, order.SellerId);

            await PostJournalAsync(db, $"sale:{order.SellerOrderId:N}", LedgerEventType.Sale, now,
                [new(LedgerAccountType.MarketplaceReceipts, null, -order.TotalVnd),
                 new(LedgerAccountType.PlatformDiscount, null, -order.PlatformDiscountVnd),
                 new(account, sellerId, order.SubtotalVnd - order.SellerDiscountVnd),
                 new(LedgerAccountType.PlatformShipping, null, order.ShippingVnd)],
                ct, sellerOrderId: order.SellerOrderId, paymentAttemptId: capture.Id);

            await PostJournalAsync(db, $"commission:{order.SellerOrderId:N}", LedgerEventType.Commission, now,
                [new(account, sellerId, -order.CommissionVnd),
                 new(LedgerAccountType.PlatformCommission, null, order.CommissionVnd)],
                ct, sellerOrderId: order.SellerOrderId, paymentAttemptId: capture.Id);
        }
    }

    public static void QueueCaptureRefund(PaymentsDbContext db, PaymentAttempt capture, string reason, DateTimeOffset now)
    {
        if (capture.CapturedAmountVnd <= 0) return;
        db.Refunds.Add(new Refund
        {
            OrderId = capture.OrderId,
            PaymentAttemptId = capture.Id,
            BusinessKey = $"{capture.OrderId:N}:{reason}:{capture.Id:N}",
            AmountVnd = capture.CapturedAmountVnd,
            RequestedAt = now,
            NextAttemptAt = capture.Method == PaymentMethod.SePay ? now : null,
            Issue = capture.Method switch
            {
                PaymentMethod.Cod => "Record the simulated COD refund reference.",
                PaymentMethod.TransportAtDelivery => "Refund through the HKShop bank account and record the confirmed reference.",
                _ => ""
            }
        });
    }

    private static async Task MarkRefundCompletedAsync(
        PaymentsDbContext db, Refund refund, string reference, DateTimeOffset now, bool simulated, CancellationToken ct)
    {
        if (await db.Refunds.AnyAsync(r => r.Id != refund.Id && r.Status == RefundStatus.Completed && r.Reference == reference, ct))
            throw new ShopRuleException("This refund reference already belongs to another refund.");

        var order = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == refund.OrderId, ct);
        refund.Status = RefundStatus.Completed;
        refund.Simulated = simulated;
        refund.CompletedAt = now;
        refund.Reference = reference;
        refund.NextAttemptAt = null;
        refund.Issue = "";

        if (refund.PaymentAttemptId is Guid captureId)
        {
            if (refund.SellerOrderId is Guid sellerOrderId)
            {
                var sellerOrder = await db.SellerOrderProjections.FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId, ct);
                if (sellerOrder is not null && await db.LedgerJournals.AnyAsync(j => j.BusinessKey == $"sale:{sellerOrderId:N}", ct))
                {
                    var (account, sellerId) = sellerOrder.SellerId == FirstPartySellerId
                        ? (LedgerAccountType.PlatformMerchandise, (long?)null)
                        : (await db.LedgerJournals.AnyAsync(j => j.BusinessKey == $"release:{sellerOrderId:N}", ct)
                            ? (LedgerAccountType.SellerAvailable, sellerOrder.SellerId)
                            : (LedgerAccountType.SellerPending, sellerOrder.SellerId));

                    var subsidy = refund.Items.Sum(i => i.PlatformDiscountReversalVnd);
                    var commission = refund.Items.Sum(i => i.CommissionReversalVnd);
                    var merchandise = refund.AmountVnd - refund.ShippingVnd + subsidy;

                    await PostJournalAsync(db, $"refund:{refund.Id}", LedgerEventType.Refund, now,
                        [new(account, sellerId, -merchandise),
                         new(LedgerAccountType.PlatformShipping, null, -refund.ShippingVnd),
                         new(LedgerAccountType.PlatformDiscount, null, subsidy),
                         new(LedgerAccountType.PaymentClearing, null, refund.AmountVnd)],
                        ct, sellerOrderId: sellerOrderId, paymentAttemptId: captureId, refundId: refund.Id);

                    await PostJournalAsync(db, $"commission-reversal:{refund.Id}", LedgerEventType.CommissionReversal, now,
                        [new(LedgerAccountType.PlatformCommission, null, -commission),
                         new(account, sellerId, commission)],
                        ct, sellerOrderId: sellerOrderId, paymentAttemptId: captureId, refundId: refund.Id);
                }
            }
            else if (await db.LedgerJournals.AnyAsync(j => j.BusinessKey == $"capture:{captureId:N}", ct))
            {
                await PostJournalAsync(db, $"refund:{refund.Id}", LedgerEventType.Refund, now,
                    [new(LedgerAccountType.MarketplaceReceipts, null, -refund.AmountVnd),
                     new(LedgerAccountType.PaymentClearing, null, refund.AmountVnd)],
                    ct, paymentAttemptId: captureId, refundId: refund.Id);
            }
        }

        db.EnqueueOutboxEvent("Refund", refund.Id.ToString(CultureInfo.InvariantCulture), nameof(RefundCompletedEvent),
            new RefundCompletedEvent(refund.Id, refund.OrderId, order?.OrderNumber ?? "", order?.CustomerEmail ?? "",
                refund.SellerOrderId, refund.PaymentAttemptId, refund.AmountVnd, refund.ShippingVnd,
                simulated, reference, now),
            $"RefundCompleted:{refund.Id}", now);
    }

    private static PaymentAttempt CreateExtraCaptureAttempt(
        PaymentsDbContext db, Guid orderId, List<PaymentAttempt> attempts, decimal expectedAmount, DateTimeOffset now)
    {
        var extra = new PaymentAttempt
        {
            OrderId = orderId,
            AttemptNumber = (attempts.Count == 0 ? 0 : attempts[^1].AttemptNumber) + 1,
            Method = PaymentMethod.SePay,
            AmountVnd = expectedAmount,
            ProviderInvoice = null,
            AppliedToOrder = false,
            CreatedAt = now
        };
        db.PaymentAttempts.Add(extra);
        attempts.Add(extra);
        return extra;
    }

    private static void ExpireOrderInternal(
        PaymentsDbContext db,
        PaymentOrderProjection order,
        Payment payment,
        List<PaymentAttempt> attempts,
        DateTimeOffset now)
    {
        order.Status = OrderStatus.Expired;
        payment.Status = PaymentStatus.Expired;
        payment.NextQueryAt = null;
        foreach (var attempt in attempts.Where(a => a.Status is PaymentStatus.Pending or PaymentStatus.Due))
        {
            attempt.Status = PaymentStatus.Expired;
            attempt.NextQueryAt = null;
        }
        db.EnqueueOutboxEvent("Order", order.OrderId.ToString("D"), nameof(PaymentExpired),
            new PaymentExpired(order.OrderId, order.OrderNumber, order.CustomerEmail, now),
            $"PaymentExpired:{order.OrderId:N}", now);
    }

    private static async Task<decimal> BalanceAsync(PaymentsDbContext db, LedgerAccountType type, long? sellerId, CancellationToken ct) =>
        await (from entry in db.LedgerEntries
               join account in db.LedgerAccounts on entry.AccountId equals account.Id
               where account.Type == type && account.SellerId == sellerId
               select entry.Side == LedgerSide.Credit ? entry.AmountVnd : -entry.AmountVnd)
            .SumAsync(ct);

    private static async Task RequireSellerFinanceAccessAsync(PaymentsDbContext db, ShopActor actor, long sellerId, CancellationToken ct)
    {
        if (actor.Admin) return;
        if (string.IsNullOrEmpty(actor.UserId) || !actor.Verified || !actor.Mfa)
            throw new ShopRuleException("You don't have access to this seller.");
        var member = await db.SellerMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SellerId == sellerId && m.UserId == actor.UserId && m.Status == SellerMembershipStatus.Active, ct);
        if (member is null || member.Role is not (SellerRole.Owner or SellerRole.Finance))
            throw new ShopRuleException("You don't have access to this seller.");
    }
}

public sealed record PaymentJobDto(Guid OrderId, string Number);
