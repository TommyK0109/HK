using System.Numerics;
using HKShop.Application.Orders;
using HKShop.Application.Settlement;
using HKShop.Domain;
using HKShop.Domain.Orders;

namespace HKShop.Application.Payments;

public sealed record RefundAllocation(long OrderItemId, int Quantity, decimal AmountVnd);
public sealed record RefundGatewayRequest(string RequestKey, string? CaptureReference, decimal AmountVnd);
public enum RefundGatewayStatus { Pending, Succeeded, NeedsReview }
public sealed record RefundGatewayOutcome(RefundGatewayStatus Status, string Reference, decimal AmountVnd,
    string Issue = "", bool Simulated = false);

public interface IRefundGateway
{
    Task<RefundGatewayOutcome> Submit(RefundGatewayRequest request, CancellationToken cancellationToken);
    Task<RefundGatewayOutcome> Query(RefundGatewayRequest request, string reference, CancellationToken cancellationToken);
}

public sealed class RefundWorkflow(IShopStore store, IRefundGateway gateway, TimeProvider time)
{
    public static async Task<bool> QueueSellerRefund(IShopSession db, Order order, SellerOrder sellerOrder,
        IReadOnlyList<RefundAllocation> allocations, decimal shippingVnd, string key, DateTimeOffset now, Guid? returnId = null)
    {
        if (sellerOrder.OrderId != order.Id) throw new ShopRuleException("Seller order does not belong to the purchase.");
        var businessKey = $"{order.Id:N}:{key}";
        if (await db.First(db.Query<Refund>().Where(r => r.BusinessKey == businessKey)) is not null) return true;

        var payment = await OrderService.FindPayment(db, order.Id);
        if (payment.Method != PaymentMethod.TransportAtDelivery && payment.Status != PaymentStatus.Paid) return false;
        var capture = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id &&
            a.AppliedToOrder && a.Status == PaymentStatus.Paid &&
            (payment.Method == PaymentMethod.TransportAtDelivery ? a.SellerOrderId == sellerOrder.Id : a.SellerOrderId == null)));
        if (capture is null && payment.Method == PaymentMethod.TransportAtDelivery) return false;
        if (capture is null && payment.Method != PaymentMethod.Cod)
            throw new ShopRuleException("The applied capture must be reconciled before issuing a refund.");

        ShopRules.Vnd(shippingVnd);
        if (allocations.Count == 0 || allocations.Select(a => a.OrderItemId).Distinct().Count() != allocations.Count)
            throw new ShopRuleException("A refund needs distinct lines from the seller order.");
        var itemIds = allocations.Select(a => a.OrderItemId).ToArray();
        var items = await db.List(db.Query<OrderItem>().Where(i =>
            i.OrderId == order.Id && i.SellerOrderId == sellerOrder.Id && itemIds.Contains(i.Id)));
        if (items.Count != allocations.Count)
            throw new ShopRuleException("A refund line does not belong to the seller order.");

        var priorLines = await db.List(db.Query<RefundItem>().Where(i =>
            i.SellerOrderId == sellerOrder.Id && itemIds.Contains(i.OrderItemId)));
        List<ReturnItem> priorReturns = returnId is null ? [] : await db.List(db.Query<ReturnItem>().Where(i =>
            i.SellerOrderId == sellerOrder.Id && i.ReturnId != returnId && itemIds.Contains(i.OrderItemId)));
        var refundLines = new List<RefundItem>();
        foreach (var allocation in allocations)
        {
            var item = items.Single(i => i.Id == allocation.OrderItemId);
            var prior = priorLines.Where(i => i.OrderItemId == item.Id).ToList();
            var previouslyAccepted = returnId is null
                ? prior.Sum(i => i.Quantity)
                : priorReturns.Where(i => i.OrderItemId == item.Id).Sum(i => i.Accepted);
            if (allocation.Quantity <= 0 || allocation.Quantity > item.Quantity - previouslyAccepted ||
                allocation.Quantity > item.Quantity - prior.Sum(i => i.Quantity) ||
                allocation.AmountVnd != item.RefundFor(previouslyAccepted, allocation.Quantity) ||
                allocation.AmountVnd > item.RefundFor(0, item.Quantity) - prior.Sum(i => i.AmountVnd))
                throw new ShopRuleException("Refund exceeds the original line quantity or amount.");

            var (previousDiscount, previousCommission) = ReversedAmounts(item, previouslyAccepted);
            var (discount, commission) = ReversedAmounts(item, previouslyAccepted + allocation.Quantity);
            refundLines.Add(new RefundItem
            {
                SellerOrderId = sellerOrder.Id,
                OrderItemId = allocation.OrderItemId,
                Quantity = allocation.Quantity,
                AmountVnd = allocation.AmountVnd,
                CommissionReversalVnd = commission - previousCommission,
                PlatformDiscountReversalVnd = discount - previousDiscount
            });
        }

        var amount = ShopRules.Vnd(allocations.Sum(a => a.AmountVnd) + shippingVnd);
        if (amount == 0)
        {
            if (capture is not null)
                await SettlementLedger.ReverseSale(db, sellerOrder, capture.Id, businessKey, 0, 0, refundLines, now);
            return false;
        }

        var purchaseRefunds = db.Query<Refund>().Where(r => r.OrderId == order.Id);
        var captureId = capture?.Id;
        var reserved = await db.Sum(purchaseRefunds.Where(r => r.PaymentAttemptId == captureId ||
            r.PaymentAttemptId == null).Select(r => r.AmountVnd));
        var sellerRefunds = purchaseRefunds.Where(r => r.SellerOrderId == sellerOrder.Id);
        var sellerReserved = await db.Sum(sellerRefunds.Select(r => r.AmountVnd));
        var shippingReserved = await db.Sum(sellerRefunds.Select(r => r.ShippingVnd));
        if (amount > (capture?.CapturedAmountVnd ?? payment.AmountVnd) - reserved ||
            amount > sellerOrder.TotalVnd - sellerReserved || shippingVnd > sellerOrder.ShippingVnd - shippingReserved)
            throw new ShopRuleException("Refund exceeds the captured payment or the seller order allocation.");

        var refund = NewRefund(order.Id, captureId, payment.Method, businessKey, amount, now);
        refund.SellerOrderId = sellerOrder.Id;
        refund.ReturnId = returnId;
        refund.ShippingVnd = shippingVnd;
        refund.Items = refundLines;
        db.Add(refund);
        return true;
    }

    private static (decimal PlatformDiscount, decimal Commission) ReversedAmounts(OrderItem item, int quantity)
    {
        var discount = decimal.Floor(item.PlatformDiscountVnd * quantity / item.Quantity);
        var gross = item.RefundFor(0, item.Quantity) + item.PlatformDiscountVnd;
        if (gross == 0) return (discount, 0);

        var reversedGross = item.RefundFor(0, quantity) + discount;
        var commission = (decimal)((BigInteger)item.CommissionVnd * (BigInteger)reversedGross / (BigInteger)gross);
        return (discount, commission);
    }

    internal static void QueueCaptureRefund(IShopSession db, PaymentAttempt capture, string reason, DateTimeOffset now)
    {
        if (capture.CapturedAmountVnd > 0)
            db.Add(NewRefund(capture.OrderId, capture.Id, capture.Method,
                $"{capture.OrderId:N}:{reason}:{capture.Id:N}", capture.CapturedAmountVnd, now));
    }

    private static Refund NewRefund(Guid orderId, Guid? attemptId, PaymentMethod method, string key,
        decimal amount, DateTimeOffset now) => new()
    {
        OrderId = orderId,
        PaymentAttemptId = attemptId,
        BusinessKey = key,
        AmountVnd = amount,
        RequestedAt = now,
        NextAttemptAt = method == PaymentMethod.SePay ? now : null,
        Issue = method switch
        {
            PaymentMethod.Cod => "Record the simulated COD refund reference.",
            PaymentMethod.TransportAtDelivery => "Refund through the HKShop bank account and record the confirmed reference.",
            _ => ""
        }
    };

    public Task<List<long>> DueRefunds() => store.Read(db =>
    {
        var now = time.GetUtcNow();
        return db.List(db.Query<Refund>().Where(r => r.Status == RefundStatus.Pending && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Id).Select(r => r.Id).Take(20));
    });

    public async Task Reconcile(long id, CancellationToken cancellationToken)
    {
        var job = await store.Write<RefundJob?>(async db =>
        {
            var now = time.GetUtcNow();
            var row = await db.First(db.Query<Refund>().Where(r => r.Id == id && r.Status == RefundStatus.Pending &&
                r.NextAttemptAt <= now));
            if (row is null) return null;
            var capture = await db.First(db.Query<PaymentAttempt>().Where(a =>
                a.Id == row.PaymentAttemptId && a.OrderId == row.OrderId && a.Status == PaymentStatus.Paid));
            if (capture is null)
            {
                row.NextAttemptAt = null;
                row.Issue = "Verify the original capture and reconcile this refund manually.";
                return null;
            }

            row.Attempts++;
            row.NextAttemptAt = now.AddMinutes(2);
            return new RefundJob(new RefundGatewayRequest(row.BusinessKey, capture.ProviderTransaction, row.AmountVnd),
                row.ProviderReference, row.Attempts);
        });
        if (job is null) return;

        RefundGatewayOutcome outcome;
        try
        {
            outcome = job.Reference.Length == 0
                ? await gateway.Submit(job.Request, cancellationToken)
                : await gateway.Query(job.Request, job.Reference, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            outcome = new RefundGatewayOutcome(RefundGatewayStatus.Pending, job.Reference, job.Request.AmountVnd,
                "Provider outcome unknown. The refund will be checked again with the same request key.");
        }

        await store.Write(async db =>
        {
            var row = await db.First(db.Query<Refund>().Where(r => r.Id == id));
            if (row is null || row.Status != RefundStatus.Pending || row.Attempts != job.Attempt) return 0;
            if (outcome.AmountVnd != row.AmountVnd || outcome.Reference.Length > 100 ||
                (job.Reference.Length > 0 && outcome.Reference != job.Reference) ||
                (outcome.Status == RefundGatewayStatus.Succeeded && outcome.Reference.Length == 0))
            {
                row.NextAttemptAt = null;
                row.Issue = "Provider returned a mismatched amount or reference. Reconcile this refund manually.";
                return 0;
            }
            row.ProviderReference = outcome.Reference;
            row.Issue = outcome.Issue;
            if (outcome.Status == RefundGatewayStatus.NeedsReview) row.NextAttemptAt = null;
            if (outcome.Status == RefundGatewayStatus.Succeeded)
                await MarkCompleted(db, row, outcome.Reference, time.GetUtcNow(), outcome.Simulated);
            return 0;
        });
    }

    internal static async Task MarkCompleted(IShopSession db, Refund refund, string reference, DateTimeOffset now, bool simulated)
    {
        var duplicate = await db.First(db.Query<Refund>().Where(r =>
            r.Id != refund.Id && r.Status == RefundStatus.Completed && r.Reference == reference));
        if (duplicate is not null)
            throw new ShopRuleException("This refund reference already belongs to another refund.");

        var order = await OrderService.FindOrder(db, refund.OrderId);
        refund.Status = RefundStatus.Completed;
        refund.Simulated = simulated;
        refund.CompletedAt = now;
        refund.Reference = reference;
        refund.NextAttemptAt = null;
        refund.Issue = "";
        if (refund.ReturnId is not null)
        {
            var request = await db.First(db.Query<ReturnRequest>().Where(r =>
                r.Id == refund.ReturnId && r.OrderId == refund.OrderId));
            if (request is not null) request.Status = ReturnStatus.Completed;
        }

        await SettlementLedger.RecordRefund(db, refund, now);
        db.Notify(order, $"refund-{refund.Id}", simulated ? "Simulated refund completed" : "Refund completed", now);
        db.IntegrationEvent("Refund", refund.Id.ToString(), "RefundCompleted", new
        {
            RefundId = refund.Id,
            refund.OrderId,
            refund.SellerOrderId,
            refund.PaymentAttemptId,
            refund.AmountVnd,
            refund.ShippingVnd,
            Simulated = simulated,
            CompletedAt = now
        }, now, $"Refund:{refund.Id}:Completed");
    }

    private sealed record RefundJob(RefundGatewayRequest Request, string Reference, int Attempt);
}
