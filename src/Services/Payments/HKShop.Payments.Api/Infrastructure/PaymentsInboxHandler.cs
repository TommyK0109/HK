using System.Text.Json;
using HKShop.Contracts.Events;
using HKShop.Domain.Orders;
using HKShop.Domain.Settlement;
using HKShop.Messaging;
using HKShop.Payments.Api.Application;
using HKShop.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Payments.Api.Infrastructure;

public sealed class PaymentsInboxHandler(TimeProvider time) : IIntegrationEventHandler<PaymentsDbContext>
{
    private const long FirstPartySellerId = 1;

    public async Task HandleAsync(PaymentsDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case nameof(PaymentRequested):
                await OnPaymentRequestedAsync(db, envelope.Payload.Deserialize<PaymentRequested>()!, ct);
                break;

            case nameof(OrderConfirmed):
                if (Guid.TryParse(envelope.AggregateId, out var confirmedOrderId))
                {
                    var proj = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == confirmedOrderId, ct);
                    if (proj is not null) proj.Status = OrderStatus.Confirmed;
                }
                break;

            case nameof(OrderCancelled):
                await OnOrderCancelledAsync(db, envelope.Payload.Deserialize<OrderCancelled>()!, ct);
                break;

            case nameof(RefundRequested):
                await OnRefundRequestedAsync(db, envelope.Payload.Deserialize<RefundRequested>()!, ct);
                break;

            case nameof(SellerOrderDelivered):
                await OnSellerOrderDeliveredAsync(db, envelope.Payload.Deserialize<SellerOrderDelivered>()!, ct);
                break;

            case nameof(DoorPaymentVerified):
                await OnDoorPaymentVerifiedAsync(db, envelope.Payload.Deserialize<DoorPaymentVerified>()!, ct);
                break;

            case nameof(ParcelDelivered):
                await OnParcelDeliveredAsync(db, envelope.Payload.Deserialize<ParcelDelivered>()!, ct);
                break;

            case nameof(ShipperFeeDue):
                await OnShipperFeeDueAsync(db, envelope.Payload.Deserialize<ShipperFeeDue>()!, ct);
                break;

            case nameof(SellerStatusChanged):
                await OnSellerStatusChangedAsync(db, envelope.Payload.Deserialize<SellerStatusChanged>()!, ct);
                break;

            case nameof(SellerMembershipChanged):
                await OnSellerMembershipChangedAsync(db, envelope.Payload.Deserialize<SellerMembershipChanged>()!, ct);
                break;
        }
    }

    private static async Task OnPaymentRequestedAsync(PaymentsDbContext db, PaymentRequested evt, CancellationToken ct)
    {
        if (!await db.OrderProjections.AnyAsync(o => o.OrderId == evt.OrderId, ct))
        {
            db.OrderProjections.Add(new PaymentOrderProjection
            {
                OrderId = evt.OrderId,
                OrderNumber = evt.OrderNumber,
                CustomerId = evt.CustomerId,
                CustomerEmail = evt.CustomerEmail,
                Status = evt.Method == PaymentMethod.SePay && evt.AmountVnd > 0 ? OrderStatus.AwaitingPayment : OrderStatus.Confirmed,
                PaymentDeadline = evt.Deadline,
                TotalVnd = evt.AmountVnd,
                CreatedAt = evt.RequestedAt
            });
        }

        foreach (var so in evt.SellerOrders)
        {
            if (!await db.SellerOrderProjections.AnyAsync(s => s.SellerOrderId == so.SellerOrderId, ct))
            {
                db.SellerOrderProjections.Add(new PaymentSellerOrderProjection
                {
                    SellerOrderId = so.SellerOrderId,
                    OrderId = evt.OrderId,
                    SellerId = so.SellerId,
                    SellerOrderNumber = so.SellerOrderNumber,
                    Status = evt.Method == PaymentMethod.SePay && evt.AmountVnd > 0 ? OrderStatus.AwaitingPayment : OrderStatus.Confirmed,
                    SubtotalVnd = so.SubtotalVnd,
                    SellerDiscountVnd = so.SellerDiscountVnd,
                    PlatformDiscountVnd = so.PlatformDiscountVnd,
                    ShippingVnd = so.ShippingVnd,
                    TotalVnd = so.TotalVnd,
                    CommissionVnd = so.CommissionVnd
                });
            }
        }

        if (!await db.Payments.AnyAsync(p => p.OrderId == evt.OrderId, ct))
        {
            var requiresOnline = evt.Method == PaymentMethod.SePay && evt.AmountVnd > 0;
            var status = requiresOnline ? PaymentStatus.Pending : (evt.Method != PaymentMethod.SePay ? PaymentStatus.Due : PaymentStatus.Paid);
            db.Payments.Add(new Payment
            {
                OrderId = evt.OrderId,
                Method = evt.Method,
                Status = status,
                AmountVnd = evt.AmountVnd,
                PaidAt = !requiresOnline && evt.Method == PaymentMethod.SePay ? evt.RequestedAt : null,
                NextQueryAt = requiresOnline ? evt.RequestedAt.AddMinutes(2) : null
            });

            if (evt.Method != PaymentMethod.TransportAtDelivery)
            {
                db.PaymentAttempts.Add(new PaymentAttempt
                {
                    OrderId = evt.OrderId,
                    AttemptNumber = 1,
                    Method = evt.Method,
                    Status = status,
                    AmountVnd = evt.AmountVnd,
                    CapturedAmountVnd = !requiresOnline && evt.Method == PaymentMethod.SePay ? evt.AmountVnd : 0,
                    ProviderInvoice = requiresOnline ? evt.OrderNumber : null,
                    AppliedToOrder = !requiresOnline && evt.Method == PaymentMethod.SePay,
                    CreatedAt = evt.RequestedAt,
                    PaidAt = !requiresOnline && evt.Method == PaymentMethod.SePay ? evt.RequestedAt : null,
                    NextQueryAt = requiresOnline ? evt.RequestedAt.AddMinutes(2) : null
                });
            }
        }
    }

    private static async Task OnOrderCancelledAsync(PaymentsDbContext db, OrderCancelled evt, CancellationToken ct)
    {
        var order = await db.OrderProjections.FirstOrDefaultAsync(o => o.OrderId == evt.OrderId, ct);
        if (order is not null) order.Status = OrderStatus.Cancelled;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment is not null && payment.Status != PaymentStatus.Paid)
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.NextQueryAt = null;
        }

        var openAttempts = await db.PaymentAttempts
            .Where(a => a.OrderId == evt.OrderId && (a.Status == PaymentStatus.Due || a.Status == PaymentStatus.Pending))
            .ToListAsync(ct);
        foreach (var attempt in openAttempts)
        {
            attempt.Status = PaymentStatus.Cancelled;
            attempt.NextQueryAt = null;
        }
    }

    private async Task OnRefundRequestedAsync(PaymentsDbContext db, RefundRequested evt, CancellationToken ct)
    {
        if (await db.Refunds.AnyAsync(r => r.BusinessKey == evt.BusinessKey, ct))
            return;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment is null || (payment.Method != PaymentMethod.TransportAtDelivery && payment.Status != PaymentStatus.Paid))
            return;

        var capture = await db.PaymentAttempts.FirstOrDefaultAsync(a =>
            a.OrderId == evt.OrderId && a.AppliedToOrder && a.Status == PaymentStatus.Paid &&
            (payment.Method == PaymentMethod.TransportAtDelivery ? a.SellerOrderId == evt.SellerOrderId : a.SellerOrderId == null), ct);

        if (evt.AmountVnd == 0)
            return;

        var now = evt.RequestedAt != default ? evt.RequestedAt : time.GetUtcNow();
        var refund = new Refund
        {
            OrderId = evt.OrderId,
            PaymentAttemptId = capture?.Id,
            SellerOrderId = evt.SellerOrderId,
            ReturnId = evt.ReturnId,
            BusinessKey = evt.BusinessKey,
            AmountVnd = evt.AmountVnd,
            ShippingVnd = evt.ShippingVnd,
            RequestedAt = now,
            NextAttemptAt = payment.Method == PaymentMethod.SePay ? now : null,
            Issue = payment.Method switch
            {
                PaymentMethod.Cod => "Record the simulated COD refund reference.",
                PaymentMethod.TransportAtDelivery => "Refund through the HKShop bank account and record the confirmed reference.",
                _ => ""
            },
            Items = evt.Lines.Select(l => new RefundItem
            {
                SellerOrderId = evt.SellerOrderId,
                OrderItemId = l.OrderItemId,
                Quantity = l.Quantity,
                AmountVnd = l.AmountVnd,
                CommissionReversalVnd = l.CommissionReversalVnd,
                PlatformDiscountReversalVnd = l.PlatformDiscountReversalVnd
            }).ToList()
        };
        db.Refunds.Add(refund);
    }

    private static async Task OnSellerOrderDeliveredAsync(PaymentsDbContext db, SellerOrderDelivered evt, CancellationToken ct)
    {
        var child = await db.SellerOrderProjections.FirstOrDefaultAsync(s => s.SellerOrderId == evt.SellerOrderId, ct);
        if (child is not null)
        {
            child.Status = OrderStatus.Delivered;
            child.DeliveredAt ??= evt.DeliveredAt;
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == evt.OrderId, ct);
        if (payment is { Method: PaymentMethod.Cod, Status: PaymentStatus.Due } && child is not null)
        {
            payment.Status = PaymentStatus.Paid;
            payment.PaidAt = evt.DeliveredAt;
            var attempt = await db.PaymentAttempts
                .Where(a => a.OrderId == evt.OrderId)
                .OrderByDescending(a => a.AttemptNumber)
                .FirstOrDefaultAsync(ct);
            if (attempt is not null && !attempt.AppliedToOrder)
            {
                attempt.Status = PaymentStatus.Paid;
                attempt.CapturedAmountVnd = payment.AmountVnd;
                attempt.PaidAt = evt.DeliveredAt;
                attempt.AppliedToOrder = true;
                await PaymentsProcessor.RecordCaptureAsync(db, attempt, evt.DeliveredAt, ct);
                await PaymentsProcessor.RecordSalesAsync(db, attempt, [child], evt.DeliveredAt, ct);
            }
        }
    }

    private static async Task OnDoorPaymentVerifiedAsync(PaymentsDbContext db, DoorPaymentVerified evt, CancellationToken ct)
    {
        var child = await db.SellerOrderProjections.FirstOrDefaultAsync(s => s.SellerOrderId == evt.SellerOrderId, ct);
        if (child is null) return;

        if (!await db.PaymentAttempts.AnyAsync(a => a.SellerOrderId == evt.SellerOrderId && a.AppliedToOrder, ct))
        {
            var attemptCount = await db.PaymentAttempts.CountAsync(a => a.OrderId == evt.OrderId, ct);
            var capture = new PaymentAttempt
            {
                OrderId = evt.OrderId,
                SellerOrderId = evt.SellerOrderId,
                AttemptNumber = attemptCount + 1,
                Method = PaymentMethod.TransportAtDelivery,
                Status = PaymentStatus.Paid,
                AmountVnd = evt.AmountVnd,
                CapturedAmountVnd = evt.AmountVnd,
                ProviderTransaction = evt.TransactionReference,
                AppliedToOrder = true,
                PaidAt = evt.VerifiedAt,
                CreatedAt = evt.VerifiedAt
            };
            db.PaymentAttempts.Add(capture);
            await db.SaveChangesAsync(ct);
            await PaymentsProcessor.RecordCaptureAsync(db, capture, evt.VerifiedAt, ct);
            await PaymentsProcessor.RecordSalesAsync(db, capture, [child], evt.VerifiedAt, ct);
        }

        if (evt.PostRetainedFee)
        {
            await PaymentsProcessor.PostJournalAsync(db, $"transport-retained:{evt.SellerOrderId:N}", LedgerEventType.ShipperFeePaid, evt.VerifiedAt,
                [new(LedgerAccountType.TransportExpense, null, -evt.ShippingVnd),
                 new(LedgerAccountType.PaymentClearing, null, evt.ShippingVnd)],
                ct, sellerOrderId: evt.SellerOrderId);
        }
    }

    private static async Task OnParcelDeliveredAsync(PaymentsDbContext db, ParcelDelivered evt, CancellationToken ct)
    {
        var child = await db.SellerOrderProjections.FirstOrDefaultAsync(s => s.SellerOrderId == evt.SellerOrderId, ct);
        if (child is not null)
        {
            child.Status = OrderStatus.Delivered;
            child.DeliveredAt ??= evt.DeliveredAt;
        }

        if (evt.DoorPayment == DoorPayment.Cash)
        {
            if (evt.FundsVerified)
            {
                await PaymentsProcessor.PostJournalAsync(db, $"transport-retained:{evt.SellerOrderId:N}", LedgerEventType.ShipperFeePaid, evt.DeliveredAt,
                    [new(LedgerAccountType.TransportExpense, null, -evt.ShipperFeeVnd),
                     new(LedgerAccountType.PaymentClearing, null, evt.ShipperFeeVnd)],
                    ct, sellerOrderId: evt.SellerOrderId);
            }
        }
        else if (evt.ShipperFeeVnd > 0)
        {
            await PaymentsProcessor.PostJournalAsync(db, $"transport-earned:{evt.SellerOrderId:N}", LedgerEventType.ShipperFeeEarned, evt.DeliveredAt,
                [new(LedgerAccountType.TransportExpense, null, -evt.ShipperFeeVnd),
                 new(LedgerAccountType.ShipperPayable, null, evt.ShipperFeeVnd)],
                ct, sellerOrderId: evt.SellerOrderId);
        }
    }

    private static async Task OnShipperFeeDueAsync(PaymentsDbContext db, ShipperFeeDue evt, CancellationToken ct)
    {
        await PaymentsProcessor.PostJournalAsync(db, $"transport-paid:{evt.SellerOrderId:N}", LedgerEventType.ShipperFeePaid, evt.PaidAt,
            [new(LedgerAccountType.ShipperPayable, null, -evt.ShipperFeeVnd),
             new(LedgerAccountType.PaymentClearing, null, evt.ShipperFeeVnd)],
            ct, sellerOrderId: evt.SellerOrderId);
        db.AddAuditEntry(evt.ActorId, "Transport shipper payout reconciled", evt.SellerOrderId.ToString(), evt.PaidAt, evt.Reference);
    }

    private static async Task OnSellerStatusChangedAsync(PaymentsDbContext db, SellerStatusChanged evt, CancellationToken ct)
    {
        var seller = await db.SellerProjections.FirstOrDefaultAsync(s => s.SellerId == evt.SellerId, ct);
        if (seller is null)
        {
            seller = new PaymentSellerProjection { SellerId = evt.SellerId };
            db.SellerProjections.Add(seller);
        }
        seller.DisplayName = evt.DisplayName;
        seller.Status = evt.Status;
    }

    private static async Task OnSellerMembershipChangedAsync(PaymentsDbContext db, SellerMembershipChanged evt, CancellationToken ct)
    {
        var member = await db.SellerMemberships.FirstOrDefaultAsync(m => m.SellerId == evt.SellerId && m.UserId == evt.UserId, ct);
        if (member is null)
        {
            member = new PaymentSellerMembershipProjection { SellerId = evt.SellerId, UserId = evt.UserId };
            db.SellerMemberships.Add(member);
        }
        member.Role = evt.Role;
        member.Status = evt.Status;
    }
}
