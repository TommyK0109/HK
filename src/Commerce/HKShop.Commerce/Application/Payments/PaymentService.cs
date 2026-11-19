using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Settlement;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;

namespace HKShop.Application.Payments;

public sealed record PaymentJob(Guid OrderId, string Number);

public sealed class PaymentService(
    IShopStore store,
    IPaymentGateway gateway,
    TimeProvider time,
    IShippingGateway? shipping = null,
    PaymentsServiceClient? remotePayments = null)
{
    private const string VoidIssue = "SePay reported a cancelled or voided transaction. Verify before processing.";

    public bool OnlineAvailable => remotePayments is not null || gateway.Available;

    public async Task<bool> IsOnlineAvailableAsync(CancellationToken ct = default) =>
        remotePayments is not null
            ? (await remotePayments.GetConfigAsync(ct)).OnlineAvailable
            : gateway.Available;

    public Task<HostedCheckout> Form(ShopActor actor, Guid id)
    {
        actor.RequireCustomer();
        if (remotePayments is not null)
            return remotePayments.GetCheckoutFormAsync(id);
        return store.Read(async db =>
        {
            var order = await OrderService.FindOrder(db, id, actor.UserId);
            order.RequireStatus(OrderStatus.AwaitingPayment);
            if (order.PaymentDeadline <= time.GetUtcNow()) throw new ShopRuleException("Payment session has expired.");
            var attempt = await db.First(db.Query<PaymentAttempt>()
                .Where(a => a.OrderId == id)
                .OrderByDescending(a => a.AttemptNumber));
            if (attempt is null || attempt.Method != PaymentMethod.SePay || attempt.Status != PaymentStatus.Pending ||
                attempt.ProviderInvoice is null || attempt.AppliedToOrder || attempt.AmountVnd <= 0)
                throw new ShopRuleException("This order cannot be paid via SePay right now.");
            return gateway.BuildCheckout(attempt.ProviderInvoice, order.Id, attempt.AmountVnd);
        });
    }

    public async Task<string> CreateRetryAttempt(ShopActor actor, Guid id)
    {
        actor.RequireCustomer();
        if (remotePayments is not null)
            return (await remotePayments.CreateRetryAttemptAsync(id)).ProviderInvoice;
        return await store.Write(async db =>
        {
            var order = await OrderService.FindOrder(db, id, actor.UserId);
            order.RequireStatus(OrderStatus.AwaitingPayment);
            var now = time.GetUtcNow();
            if (order.PaymentDeadline <= now) throw new ShopRuleException("Payment session has expired.");
            var payment = await OrderService.FindPayment(db, id);
            if (payment.Method != PaymentMethod.SePay || order.TotalVnd <= 0)
                throw new ShopRuleException("This order does not support SePay retry.");

            var attempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == id).OrderBy(a => a.AttemptNumber));
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
                OrderId = order.Id,
                AttemptNumber = nextNumber,
                Method = PaymentMethod.SePay,
                Status = PaymentStatus.Pending,
                AmountVnd = order.TotalVnd,
                CreatedAt = now,
                NextQueryAt = now.AddMinutes(2)
            };
            retry.ProviderInvoice = $"HP{retry.Id:N}".ToUpperInvariant();
            db.Add(retry);
            payment.Status = PaymentStatus.Pending;
            payment.NextQueryAt = retry.NextQueryAt;
            order.PaymentStatus = PaymentStatus.Pending;
            return retry.ProviderInvoice;
        });
    }

    public Task<bool> Apply(GatewayOutcome outcome) => store.Write(async db =>
    {
        var attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.ProviderInvoice == outcome.Invoice));
        if (attempt is null)
        {
            var legacyOrder = await db.First(db.Query<Order>().Where(o => o.Number == outcome.Invoice));
            if (legacyOrder is null) return false;
            attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == legacyOrder.Id).OrderBy(a => a.AttemptNumber));
            if (attempt is null) return false;
        }

        var order = await OrderService.FindOrder(db, attempt.OrderId);
        var payment = await OrderService.FindPayment(db, order.Id);
        if (attempt.Method != PaymentMethod.SePay || payment.Method != PaymentMethod.SePay) return false;

        var now = time.GetUtcNow();
        var eventKey = outcome.Paid
            ? $"SePay:{outcome.Transaction}:paid"
            : (string.IsNullOrEmpty(outcome.Transaction)
                ? $"SePay:{attempt.ProviderInvoice}:failed"
                : $"SePay:{outcome.Transaction}:failed");

        var existingEvent = await db.First(db.Query<PaymentEvent>().Where(e => e.EventKey == eventKey));
        if (existingEvent is not null) return existingEvent.OrderId == order.Id;

        if (!string.IsNullOrEmpty(outcome.Transaction))
        {
            var txAttempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.ProviderTransaction == outcome.Transaction));
            if (txAttempt is not null) return txAttempt.OrderId == order.Id;
        }

        if (outcome.Currency != "VND")
        {
            attempt.Issue = "Payment currency mismatch.";
            payment.Issue = "Payment currency mismatch.";
            return false;
        }

        var attempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id).OrderBy(a => a.AttemptNumber));
        var sellerOrders = await db.List(db.Query<SellerOrder>().Where(s => s.OrderId == order.Id).OrderBy(s => s.SellerId));

        if (!outcome.Paid)
        {
            if (attempt.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            {
                attempt.Status = PaymentStatus.Failed;
                attempt.NextQueryAt = null;
            }
            db.Add(new PaymentEvent
            {
                OrderId = order.Id,
                PaymentAttemptId = attempt.Id,
                EventType = "Failed",
                EventKey = eventKey,
                ReceivedAt = now
            });

            var hasOtherOpenOrPaid = attempts.Any(a => a.Id != attempt.Id && (a.AppliedToOrder || a.Status == PaymentStatus.Pending));
            if (order.Status == OrderStatus.AwaitingPayment && !hasOtherOpenOrPaid)
            {
                await OrderService.Release(db, order, now);
                order.ChangeStatus(OrderStatus.PaymentFailed, now);
                order.PaymentStatus = PaymentStatus.Failed;
                foreach (var so in sellerOrders)
                {
                    so.ChangeStatus(OrderStatus.PaymentFailed, now);
                    await OrderService.CloseTransport(db, so, now, shipping);
                }
                payment.Status = PaymentStatus.Failed;
                payment.NextQueryAt = null;
                EmitSellerOrdersCancelled(db, sellerOrders, "PaymentFailed", now);
                db.Notify(order, "payment-failed", "Payment failed", now);
            }
            return true;
        }

        var targetAttempt = attempt.ProviderTransaction is null && attempt.Status != PaymentStatus.Paid
            ? attempt
            : CreateExtraCaptureAttempt(db, order, attempts, attempt.AmountVnd, now);

        targetAttempt.Status = PaymentStatus.Paid;
        targetAttempt.CapturedAmountVnd = outcome.Amount;
        targetAttempt.ProviderTransaction = outcome.Transaction;
        targetAttempt.PaidAt = now;
        targetAttempt.NextQueryAt = null;
        await SettlementLedger.RecordCapture(db, targetAttempt, now);

        var appliedAttempt = attempts.FirstOrDefault(a => a.AppliedToOrder);
        if (appliedAttempt is not null)
        {
            targetAttempt.AppliedToOrder = false;
            targetAttempt.Issue = $"Excess capture ({outcome.Amount:N0} VND) received for an already paid order.";
            payment.Issue = "Excess capture recorded for reconciliation.";
            db.Add(new PaymentEvent
            {
                OrderId = order.Id,
                PaymentAttemptId = targetAttempt.Id,
                EventType = "ExcessCapture",
                EventKey = eventKey,
                ReceivedAt = now
            });
            RefundWorkflow.QueueCaptureRefund(db, targetAttempt, "excess", now);
            db.IntegrationEvent("PaymentAttempt", targetAttempt.Id.ToString("D"), "PaymentExcessCaptured", new
            {
                PaymentAttemptId = targetAttempt.Id,
                OrderId = order.Id,
                order.Number,
                targetAttempt.CapturedAmountVnd,
                targetAttempt.ProviderTransaction,
                CapturedAt = now
            }, now, $"PaymentAttempt:{targetAttempt.Id:N}:Excess");
            db.Notify(order, $"excess-{targetAttempt.Id:N}", "Excess payment captured and queued for refund", now);
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
            db.Add(new PaymentEvent
            {
                OrderId = order.Id,
                PaymentAttemptId = targetAttempt.Id,
                EventType = isOverpayment ? "ExcessCapture" : "AmountMismatch",
                EventKey = eventKey,
                ReceivedAt = now
            });
            RefundWorkflow.QueueCaptureRefund(db, targetAttempt, "mismatch", now);
            if (order.Status == OrderStatus.AwaitingPayment && order.PaymentDeadline <= now)
                await ExpireOrder(db, order, sellerOrders, payment, attempts, now, shipping);
            db.IntegrationEvent("PaymentAttempt", targetAttempt.Id.ToString("D"), "PaymentReconciliationRequired", new
            {
                PaymentAttemptId = targetAttempt.Id,
                OrderId = order.Id,
                order.Number,
                ExpectedAmountVnd = attempt.AmountVnd,
                targetAttempt.CapturedAmountVnd,
                targetAttempt.ProviderTransaction,
                CapturedAt = now
            }, now, $"PaymentAttempt:{targetAttempt.Id:N}:Mismatch");
            db.Notify(order, $"mismatch-{targetAttempt.Id:N}", "Captured amount does not match order total; queued for reconciliation", now);
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

            order.PaymentStatus = PaymentStatus.Paid;
            order.ChangeStatus(OrderStatus.Confirmed, now);
            foreach (var so in sellerOrders) so.ChangeStatus(OrderStatus.Confirmed, now);

            await SettlementLedger.RecordSales(db, targetAttempt, sellerOrders, now);
            await OrderService.SetClaim(db, order.Id, ClaimStatus.Redeemed);
            db.Add(new PaymentEvent
            {
                OrderId = order.Id,
                PaymentAttemptId = targetAttempt.Id,
                EventType = "Captured",
                EventKey = eventKey,
                ReceivedAt = now
            });
            db.IntegrationEvent("PaymentAttempt", targetAttempt.Id.ToString("D"), "PaymentCaptured", new
            {
                PaymentAttemptId = targetAttempt.Id,
                OrderId = order.Id,
                order.Number,
                targetAttempt.CapturedAmountVnd,
                targetAttempt.ProviderTransaction,
                PaidAt = now
            }, now, $"PaymentAttempt:{targetAttempt.Id:N}:Captured");
            CheckoutService.EmitOrderConfirmed(db, order, now);
            return true;
        }

        if (order.Status == OrderStatus.AwaitingPayment)
            await ExpireOrder(db, order, sellerOrders, payment, attempts, now, shipping);

        targetAttempt.AppliedToOrder = false;
        targetAttempt.Issue = $"Late capture received while order status was {order.Status}.";
        payment.Issue = "Late capture recorded after order expired or closed; refund queued for reconciliation.";
        payment.NextQueryAt = null;
        db.Add(new PaymentEvent
        {
            OrderId = order.Id,
            PaymentAttemptId = targetAttempt.Id,
            EventType = "LateCapture",
            EventKey = eventKey,
            ReceivedAt = now
        });
        RefundWorkflow.QueueCaptureRefund(db, targetAttempt, "late", now);
        db.IntegrationEvent("PaymentAttempt", targetAttempt.Id.ToString("D"), "PaymentLateCaptured", new
        {
            PaymentAttemptId = targetAttempt.Id,
            OrderId = order.Id,
            order.Number,
            OrderStatus = order.Status.ToString(),
            targetAttempt.CapturedAmountVnd,
            targetAttempt.ProviderTransaction,
            CapturedAt = now
        }, now, $"PaymentAttempt:{targetAttempt.Id:N}:Late");
        db.Notify(order, $"late-payment-{targetAttempt.Id:N}", "Late payment received after order closed; queued for refund", now);
        return true;
    });

    public Task<int> FlagVoid(string invoice) => store.Write(async db =>
    {
        var attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.ProviderInvoice == invoice));
        Order? order;
        if (attempt is not null)
        {
            order = await OrderService.FindOrder(db, attempt.OrderId);
        }
        else
        {
            order = await db.First(db.Query<Order>().Where(o => o.Number == invoice));
            if (order is null) return 0;
            attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id).OrderByDescending(a => a.AttemptNumber));
        }

        var payment = await OrderService.FindPayment(db, order.Id);
        if (payment.Method == PaymentMethod.SePay)
        {
            payment.Issue = VoidIssue;
            if (attempt is not null) attempt.Issue = VoidIssue;
        }
        return 0;
    });

    public Task<int> Expire() => store.Write(async db =>
    {
        var now = time.GetUtcNow();
        var due = await db.List(db.Query<Order>().Where(o => o.Status == OrderStatus.AwaitingPayment && o.PaymentDeadline <= now)
            .OrderBy(o => o.PaymentDeadline).Take(25));
        foreach (var order in due)
        {
            var payment = await OrderService.FindPayment(db, order.Id);
            var sellerOrders = await db.List(db.Query<SellerOrder>().Where(s => s.OrderId == order.Id).OrderBy(s => s.SellerId));
            var attempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id));
            await ExpireOrder(db, order, sellerOrders, payment, attempts, now, shipping);
            db.Notify(order, "expired", "Order payment expired", now);
        }
        return due.Count;
    });

    public Task<List<PaymentJob>> DueQueries() => store.Read(db =>
    {
        var now = time.GetUtcNow();
        return db.List((from attempt in db.Query<PaymentAttempt>()
            where attempt.Method == PaymentMethod.SePay && attempt.ProviderInvoice != null && attempt.NextQueryAt <= now
            orderby attempt.NextQueryAt
            select new PaymentJob(attempt.OrderId, attempt.ProviderInvoice!)).Take(20));
    });

    public async Task Reconcile(Guid id, string invoice, CancellationToken cancellationToken)
    {
        GatewayOutcome? outcome = null;
        Exception? failure = null;
        try { outcome = await gateway.Query(invoice, cancellationToken); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            failure = error;
        }
        if (outcome is not null && await Apply(outcome) && outcome.Paid)
        {
            await ClearConfirmedVoidIssue(id, outcome);
            return;
        }

        await store.Write(async db =>
        {
            var order = await OrderService.FindOrder(db, id);
            var payment = await OrderService.FindPayment(db, id);
            var attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == id && a.ProviderInvoice == invoice));
            if (payment.Status == PaymentStatus.Paid && (attempt is null || attempt.Status == PaymentStatus.Paid)) return 0;

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
            return 0;
        });
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private Task<int> ClearConfirmedVoidIssue(Guid orderId, GatewayOutcome outcome) => store.Write(async db =>
    {
        var capture = await db.First(db.Query<PaymentAttempt>().Where(attempt =>
            attempt.OrderId == orderId && attempt.Status == PaymentStatus.Paid && attempt.AppliedToOrder &&
            attempt.Issue == VoidIssue && attempt.ProviderTransaction == outcome.Transaction &&
            attempt.CapturedAmountVnd == outcome.Amount && outcome.Currency == "VND"));
        if (capture is null) return 0;
        capture.Issue = "";
        var payment = await OrderService.FindPayment(db, orderId);
        if (payment.Issue == VoidIssue) payment.Issue = "";
        return 1;
    });

    public async Task Reconcile(ShopActor actor, Guid orderId, CancellationToken cancellationToken)
    {
        actor.RequireAdmin();
        var invoices = await store.Write(async db =>
        {
            var order = await OrderService.FindOrder(db, orderId);
            var payment = await OrderService.FindPayment(db, orderId);
            if (payment.Method != PaymentMethod.SePay) throw new ShopRuleException("This order does not use SePay.");
            db.Audit(actor.UserId, "Đối soát SePay", order.Number, time.GetUtcNow(), sellerId: order.SellerId);
            var list = await db.List(db.Query<PaymentAttempt>()
                .Where(a => a.OrderId == orderId && a.ProviderInvoice != null)
                .OrderByDescending(a => a.AttemptNumber)
                .Select(a => a.ProviderInvoice!));
            return list.Count > 0 ? list : [order.Number];
        });
        foreach (var invoice in invoices.Distinct())
            await Reconcile(orderId, invoice, cancellationToken);
    }

    public async Task<List<PaymentIssue>> Issues(ShopActor actor)
    {
        actor.RequireAdmin();
        if (remotePayments is not null)
            return (await remotePayments.GetIssuesAsync()).ToList();
        return await store.Read(db => db.List((from attempt in db.Query<PaymentAttempt>()
            join order in db.Query<Order>() on attempt.OrderId equals order.Id
            join payment in db.Query<Payment>() on order.Id equals payment.OrderId
            where attempt.Method == PaymentMethod.SePay
                && (attempt.Issue != "" || attempt.Status == PaymentStatus.Pending || payment.Issue != "")
            orderby order.CreatedAt descending, attempt.AttemptNumber descending
            select new PaymentIssue(
                order.Id,
                order.Number,
                attempt.ProviderInvoice ?? order.Number,
                attempt.Status,
                attempt.AmountVnd,
                attempt.CapturedAmountVnd,
                attempt.Issue != "" ? attempt.Issue : payment.Issue,
                attempt.CreatedAt)).Take(100)));
    }

    public async Task<List<Refund>> PendingRefunds(ShopActor actor)
    {
        actor.RequireAdmin();
        if (remotePayments is not null)
        {
            return (await remotePayments.GetPendingRefundsAsync()).Select(r => new Refund
            {
                Id = r.Id, OrderId = r.OrderId, PaymentAttemptId = r.PaymentAttemptId, SellerOrderId = r.SellerOrderId,
                ReturnId = r.ReturnId, BusinessKey = r.BusinessKey, AmountVnd = r.AmountVnd, ShippingVnd = r.ShippingVnd,
                Status = r.Status, Simulated = r.Simulated, Reference = r.Reference, ProviderReference = r.ProviderReference,
                Issue = r.Issue, NextAttemptAt = r.NextAttemptAt, RequestedAt = r.RequestedAt, CompletedAt = r.CompletedAt
            }).ToList();
        }
        return await store.Read(db => db.List(db.Query<Refund>().Where(r => r.Status == RefundStatus.Pending)
            .OrderBy(r => r.RequestedAt).Take(100)));
    }

    public async Task<int> CompleteRefund(ShopActor actor, long refundId, string reference)
    {
        actor.RequireAdmin();
        reference = ShopRules.Text(reference, 100, "Confirmed refund reference");
        if (remotePayments is not null)
        {
            await remotePayments.CompleteRefundAsync(refundId, reference);
            return 0;
        }
        return await store.Write(async db =>
        {
            var row = await db.First(db.Query<Refund>().Where(r => r.Id == refundId))
                ?? throw new ShopRuleException("Refund request not found.");
            if (row.Status == RefundStatus.Completed) return 0;
            if (row.NextAttemptAt is not null)
                throw new ShopRuleException("This refund is still scheduled for automatic reconciliation.");
            if (row.ProviderReference.Length > 0 && row.ProviderReference != reference)
                throw new ShopRuleException("The confirmed reference must match the provider refund reference.");
            var payment = await OrderService.FindPayment(db, row.OrderId);
            var capture = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == row.OrderId &&
                (row.PaymentAttemptId == null ? a.AppliedToOrder : a.Id == row.PaymentAttemptId)));
            var captured = capture is { Status: PaymentStatus.Paid } ? capture.CapturedAmountVnd :
                capture is null && row.PaymentAttemptId is null && payment.Status == PaymentStatus.Paid ? payment.AmountVnd : 0;
            var attemptId = capture?.Id;
            var includeLegacy = capture is null || capture.AppliedToOrder;
            var reserved = await db.Sum(db.Query<Refund>().Where(r => r.OrderId == row.OrderId &&
                (r.PaymentAttemptId == attemptId || (r.PaymentAttemptId == null && includeLegacy)))
                .Select(r => r.AmountVnd));
            if (captured == 0 || reserved > captured)
                throw new ShopRuleException("Reconcile the captured payment before confirming this refund.");
            var seller = row.SellerOrderId is null ? null : await db.First(db.Query<SellerOrder>().Where(s =>
                s.Id == row.SellerOrderId && s.OrderId == row.OrderId));
            var now = time.GetUtcNow();
            var simulated = payment.Method == PaymentMethod.Cod;
            await RefundWorkflow.MarkCompleted(db, row, reference, now, simulated);
            var action = simulated ? "Hoàn tiền mô phỏng" : "Xác nhận đối soát hoàn tiền";
            db.Audit(actor.UserId, action, row.Id.ToString(), now, reference, seller?.SellerId);
            return 0;
        });
    }

    private static PaymentAttempt CreateExtraCaptureAttempt(IShopSession db, Order order, List<PaymentAttempt> attempts,
        decimal expectedAmount, DateTimeOffset now)
    {
        var extra = new PaymentAttempt
        {
            OrderId = order.Id,
            AttemptNumber = (attempts.Count == 0 ? 0 : attempts[^1].AttemptNumber) + 1,
            Method = PaymentMethod.SePay,
            AmountVnd = expectedAmount,
            ProviderInvoice = null,
            AppliedToOrder = false,
            CreatedAt = now
        };
        db.Add(extra);
        attempts.Add(extra);
        return extra;
    }

    private static async Task ExpireOrder(IShopSession db, Order order, List<SellerOrder> sellerOrders,
        Payment payment, List<PaymentAttempt> attempts, DateTimeOffset now, IShippingGateway? shipping = null)
    {
        await OrderService.Release(db, order, now);
        order.ChangeStatus(OrderStatus.Expired, now);
        order.PaymentStatus = PaymentStatus.Expired;
        payment.Status = PaymentStatus.Expired;
        foreach (var so in sellerOrders)
        {
            so.ChangeStatus(OrderStatus.Expired, now);
            await OrderService.CloseTransport(db, so, now, shipping);
        }
        foreach (var attempt in attempts.Where(a => a.Status is PaymentStatus.Pending or PaymentStatus.Due))
            attempt.Status = PaymentStatus.Expired;
        EmitSellerOrdersCancelled(db, sellerOrders, "PaymentExpired", now);
        db.IntegrationEvent("Order", order.Id.ToString("D"), "OrderExpired", new
        {
            OrderId = order.Id,
            order.Number,
            ExpiredAt = now
        }, now, $"Order:{order.Id:N}:Expired");
    }

    private static void EmitSellerOrdersCancelled(IShopSession db, IEnumerable<SellerOrder> sellerOrders, string reason, DateTimeOffset now)
    {
        foreach (var so in sellerOrders)
        {
            db.IntegrationEvent("SellerOrder", so.Id.ToString("D"), "SellerOrderCancelled", new
            {
                SellerOrderId = so.Id,
                so.OrderId,
                so.SellerId,
                so.Number,
                Reason = reason,
                CancelledAt = now
            }, now, $"SellerOrder:{so.Id:N}:{reason}");
        }
    }
}
