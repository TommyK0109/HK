using System.Security.Cryptography;
using System.Text.Json;
using HKShop.Application.Transportation;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Messaging;
using HKShop.Ordering.Api.Application;
using HKShop.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Ordering.Api.Infrastructure;

public sealed class OrderingInboxHandler(TimeProvider time) : IIntegrationEventHandler<OrderingDbContext>
{
    public async Task HandleAsync(OrderingDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case nameof(StockReserved):
                await OnStockReservedAsync(db, envelope.Payload.Deserialize<StockReserved>()!, ct);
                break;

            case nameof(StockReservationFailed):
                await OnStockReservationFailedAsync(db, envelope.Payload.Deserialize<StockReservationFailed>()!, ct);
                break;

            case nameof(PaymentCaptured):
                await OnPaymentCapturedAsync(db, envelope.Payload.Deserialize<PaymentCaptured>()!, ct);
                break;

            case nameof(PaymentFailed):
                await OnPaymentTerminatedAsync(db, envelope.Payload.Deserialize<PaymentFailed>()!.OrderId, OrderStatus.PaymentFailed, PaymentStatus.Failed, "PaymentFailed", ct);
                break;

            case nameof(PaymentExpired):
                await OnPaymentTerminatedAsync(db, envelope.Payload.Deserialize<PaymentExpired>()!.OrderId, OrderStatus.Expired, PaymentStatus.Expired, "PaymentExpired", ct);
                break;

            case nameof(ParcelPickedUp):
                await OnParcelPickedUpAsync(db, envelope.Payload.Deserialize<ParcelPickedUp>()!, ct);
                break;

            case nameof(ParcelDelivered):
                await OnParcelDeliveredAsync(db, envelope.Payload.Deserialize<ParcelDelivered>()!, ct);
                break;

            case nameof(PickupDeadlineExpired):
                await OnPickupDeadlineExpiredAsync(db, envelope.Payload.Deserialize<PickupDeadlineExpired>()!, ct);
                break;

            case nameof(ParcelReturnedToSender):
                await OnParcelReturnedToSenderAsync(db, envelope.Payload.Deserialize<ParcelReturnedToSender>()!, ct);
                break;

            case nameof(DoorPaymentVerified):
                await OnDoorPaymentVerifiedAsync(db, envelope.Payload.Deserialize<DoorPaymentVerified>()!, ct);
                break;

            case nameof(RefundCompletedEvent):
                await OnRefundCompletedAsync(db, envelope.Payload.Deserialize<RefundCompletedEvent>()!, ct);
                break;

            case nameof(OfferChanged):
                await OnOfferChangedAsync(db, envelope.Payload.Deserialize<OfferChanged>()!, ct);
                break;

            case nameof(SellerMembershipChanged):
                await OnSellerMembershipChangedAsync(db, envelope.Payload.Deserialize<SellerMembershipChanged>()!, ct);
                break;
        }
    }

    private async Task OnStockReservedAsync(OrderingDbContext db, StockReserved evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        if (order is null || order.Status != OrderStatus.AwaitingPayment)
            return;

        var now = evt.ReservedAt != default ? evt.ReservedAt : time.GetUtcNow();

        // Remove only purchased lines if cart matches or still holds them
        var cart = await db.ShoppingCarts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.CustomerId == order.CustomerId, ct);
        if (cart is not null && (order.QuotedCartRevision is null || cart.Revision == order.QuotedCartRevision))
        {
            var reservedOffers = evt.Lines.Select(l => l.OfferId).ToHashSet();
            cart.Items.RemoveAll(i => reservedOffers.Contains(i.OfferId));
            cart.Revision = Guid.NewGuid();
            cart.UpdatedAt = now;
        }

        IReadOnlyList<TransportRoute>? routes = null;
        if (!string.IsNullOrWhiteSpace(order.QuotedCartSnapshotJson))
            routes = JsonSerializer.Deserialize<List<TransportRoute>>(order.QuotedCartSnapshotJson);

        var requiresOnlineCapture = order.PaymentMethod == PaymentMethod.SePay && order.TotalVnd > 0;
        var sellerSplits = order.SellerOrders.Select(so => new PaymentSellerSplit(
            so.Id, so.SellerId, so.Number, so.SubtotalVnd, so.SellerDiscountVnd, so.PlatformDiscountVnd,
            so.ShippingVnd, so.TotalVnd, so.CommissionVnd)).ToList();

        if (!requiresOnlineCapture)
        {
            order.ChangeStatus(OrderStatus.Confirmed, now);
            foreach (var so in order.SellerOrders)
                so.ChangeStatus(OrderStatus.Confirmed, now);
            var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
            if (claim is not null) claim.Status = ClaimStatus.Redeemed;

            EmitConfirmedEvents(db, order, now);
        }

        foreach (var so in order.SellerOrders.OrderBy(s => s.SellerId))
        {
            NewTransportParcel? parcel = null;
            var route = routes?.FirstOrDefault(r => r.SellerId == so.SellerId);
            if (route is not null)
            {
                var deliveryCode = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
                parcel = new NewTransportParcel(so.Id, route.LocationId, route.PickupAddress,
                    route.Origin, route.Destination, route.DistanceMeters, deliveryCode);
            }
            db.EnqueueOutboxEvent("SellerOrder", so.Id.ToString("D"), nameof(SellerOrderPlaced),
                new SellerOrderPlaced(so.Id, order.Id, so.SellerId, so.Number, so.Status, so.TotalVnd, so.ShippingVnd,
                    order.PaymentMethod, order.PaymentStatus, order.Recipient, order.Phone, order.ShippingAddress, parcel, now),
                $"SellerOrderPlaced:{so.Id:N}", now);
        }

        db.EnqueueOutboxEvent("Order", order.Id.ToString("D"), nameof(PaymentRequested),
            new PaymentRequested(order.Id, order.Number, order.CustomerId, order.CustomerEmail,
                order.PaymentMethod, order.TotalVnd, order.PaymentDeadline ?? now.AddMinutes(15), sellerSplits, now),
            $"PaymentRequested:{order.Id:N}", now);
    }

    private async Task OnStockReservationFailedAsync(OrderingDbContext db, StockReservationFailed evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        if (order is null || order.Status != OrderStatus.AwaitingPayment)
            return;

        var now = evt.FailedAt != default ? evt.FailedAt : time.GetUtcNow();
        order.ChangeStatus(OrderStatus.Cancelled, now, evt.Reason);
        foreach (var so in order.SellerOrders)
            so.ChangeStatus(OrderStatus.Cancelled, now);

        var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
        if (claim is not null) claim.Status = ClaimStatus.Released;
        // Buyer cart is intentionally retained at its quoted revision!
    }

    private async Task OnPaymentCapturedAsync(OrderingDbContext db, PaymentCaptured evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        if (order is null || order.Status != OrderStatus.AwaitingPayment)
            return;

        var now = evt.PaidAt != default ? evt.PaidAt : time.GetUtcNow();
        order.PaymentStatus = PaymentStatus.Paid;
        order.ChangeStatus(OrderStatus.Confirmed, now);
        foreach (var so in order.SellerOrders)
            so.ChangeStatus(OrderStatus.Confirmed, now);

        var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
        if (claim is not null) claim.Status = ClaimStatus.Redeemed;

        EmitConfirmedEvents(db, order, now);
    }

    private async Task OnPaymentTerminatedAsync(
        OrderingDbContext db, Guid orderId, OrderStatus targetStatus, PaymentStatus paymentStatus, string reason, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || order.Status != OrderStatus.AwaitingPayment)
            return;

        var now = time.GetUtcNow();
        order.PaymentStatus = paymentStatus;
        order.ChangeStatus(targetStatus, now, reason);
        foreach (var so in order.SellerOrders)
            so.ChangeStatus(targetStatus, now);

        var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
        if (claim is not null) claim.Status = ClaimStatus.Released;

        db.EnqueueOutboxEvent("Order", order.Id.ToString("D"), nameof(OrderCancelled),
            new OrderCancelled(order.Id, order.Number, order.CustomerEmail, reason,
                order.SellerOrders.Select(s => s.Id).ToList(), now),
            $"OrderCancelled:{order.Id:N}:{reason}", now);
    }

    private async Task OnParcelPickedUpAsync(OrderingDbContext db, ParcelPickedUp evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        var child = order?.SellerOrders.FirstOrDefault(s => s.Id == evt.SellerOrderId);
        if (order is null || child is null || child.Status != OrderStatus.Packing)
            return;

        var now = evt.PickedUpAt != default ? evt.PickedUpAt : time.GetUtcNow();
        if (!await db.Shipments.AnyAsync(s => s.SellerOrderId == child.Id, ct))
        {
            db.Shipments.Add(new Shipment
            {
                OrderId = order.Id,
                SellerOrderId = child.Id,
                Carrier = "HKShop",
                TrackingNumber = child.Number,
                DispatchedAt = now
            });
        }
        child.ChangeStatus(OrderStatus.Shipped, now);
        await OrderingSagaOrchestrator.UpdateParentSummaryAsync(db, order, now, ct);

        var lines = order.Items.Where(i => i.SellerOrderId == child.Id)
            .Select(i => new StockReservationLine(i.OfferId, i.SkuId, i.SellerId, i.Quantity, i.UnitPriceVnd)).ToList();
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), nameof(SellerOrderDispatched),
            new SellerOrderDispatched(child.Id, order.Id, child.SellerId, child.Number, order.CustomerEmail, "HKShop", child.Number, lines, now),
            $"SellerOrderDispatched:{child.Id:N}", now);
    }

    private async Task OnParcelDeliveredAsync(OrderingDbContext db, ParcelDelivered evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        var child = order?.SellerOrders.FirstOrDefault(s => s.Id == evt.SellerOrderId);
        if (order is null || child is null || child.Status != OrderStatus.Shipped)
            return;

        var now = evt.DeliveredAt != default ? evt.DeliveredAt : time.GetUtcNow();
        child.ChangeStatus(OrderStatus.Delivered, now);
        child.DeliveredAt = now;
        await OrderingSagaOrchestrator.UpdateParentSummaryAsync(db, order, now, ct);

        var productIds = order.Items.Where(i => i.SellerOrderId == child.Id).Select(i => i.ProductId).Where(p => p > 0).Distinct().ToList();
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), nameof(SellerOrderDelivered),
            new SellerOrderDelivered(child.Id, order.Id, child.SellerId, child.Number, order.CustomerId, order.CustomerEmail, productIds, now),
            $"SellerOrderDelivered:{child.Id:N}", now);
    }

    private async Task OnPickupDeadlineExpiredAsync(OrderingDbContext db, PickupDeadlineExpired evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        var child = order?.SellerOrders.FirstOrDefault(s => s.Id == evt.SellerOrderId);
        if (order is null || child is null || child.Status == OrderStatus.Cancelled)
            return;

        var now = evt.ExpiredAt != default ? evt.ExpiredAt : time.GetUtcNow();
        child.ChangeStatus(OrderStatus.Cancelled, now);
        OrderingSagaOrchestrator.QueueFullSellerRefund(db, order, child, "cancel", now);
        await OrderingSagaOrchestrator.UpdateParentSummaryAsync(db, order, now, ct);

        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), "SellerOrderCancelled",
            new { SellerOrderId = child.Id, OrderId = order.Id, child.SellerId, child.Number, Reason = "PickupDeadlineExpired", CancelledAt = now },
            $"SellerOrder:{child.Id:N}:PickupDeadlineExpired", now);
    }

    private async Task OnParcelReturnedToSenderAsync(OrderingDbContext db, ParcelReturnedToSender evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Events).FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        if (order is null) return;
        order.Events.Add(new OrderEvent
        {
            OrderId = order.Id,
            Status = order.Status,
            OccurredAt = evt.OccurredAt != default ? evt.OccurredAt : time.GetUtcNow(),
            Note = $"Returning to seller: {evt.Issue}"
        });
    }

    private static async Task OnDoorPaymentVerifiedAsync(OrderingDbContext db, DoorPaymentVerified evt, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.SellerOrders).FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);
        if (order is null) return;
        order.PaymentStatus = PaymentStatus.Paid;
    }

    private static async Task OnRefundCompletedAsync(OrderingDbContext db, RefundCompletedEvent evt, CancellationToken ct)
    {
        if (evt.SellerOrderId is not Guid sellerOrderId) return;
        var returns = await db.ReturnRequests
            .Where(r => r.OrderId == evt.OrderId && r.SellerOrderId == sellerOrderId && r.Status == ReturnStatus.AwaitingRefund)
            .ToListAsync(ct);
        foreach (var r in returns)
            r.Status = ReturnStatus.Completed;
    }

    private static async Task OnOfferChangedAsync(OrderingDbContext db, OfferChanged evt, CancellationToken ct)
    {
        var row = await db.OfferProjections.FirstOrDefaultAsync(o => o.OfferId == evt.OfferId, ct);
        if (row is null)
        {
            row = new OrderingOfferProjection { OfferId = evt.OfferId };
            db.OfferProjections.Add(row);
        }
        row.SkuId = evt.SkuId;
        row.SellerId = evt.SellerId;
        row.SellerName = evt.SellerName;
        row.SellerSkuCode = evt.SellerSkuCode;
        row.Slug = evt.Slug;
        row.Name = evt.Name;
        row.PriceVnd = evt.PriceVnd;
        row.Available = evt.Available;
        row.Active = evt.Active;
    }

    private static async Task OnSellerMembershipChangedAsync(OrderingDbContext db, SellerMembershipChanged evt, CancellationToken ct)
    {
        var row = await db.SellerMemberships.FirstOrDefaultAsync(m => m.SellerId == evt.SellerId && m.UserId == evt.UserId, ct);
        if (row is null)
        {
            row = new OrderingSellerMembershipProjection { SellerId = evt.SellerId, UserId = evt.UserId };
            db.SellerMemberships.Add(row);
        }
        row.Role = evt.Role;
        row.Status = evt.Status;
    }

    private static void EmitConfirmedEvents(OrderingDbContext db, Order order, DateTimeOffset now)
    {
        db.EnqueueOutboxEvent("Order", order.Id.ToString("D"), nameof(OrderConfirmed),
            new OrderConfirmed(order.Number, order.CustomerEmail), $"Order:{order.Id:N}:Confirmed", now);

        foreach (var so in order.SellerOrders.OrderBy(s => s.SellerId))
        {
            db.EnqueueOutboxEvent("SellerOrder", so.Id.ToString("D"), "SellerOrderConfirmed", new
            {
                SellerOrderId = so.Id,
                so.OrderId,
                so.SellerId,
                so.Number,
                so.SubtotalVnd,
                so.SellerDiscountVnd,
                so.PlatformDiscountVnd,
                so.ShippingVnd,
                so.TotalVnd,
                so.CommissionVnd,
                ConfirmedAt = now
            }, $"SellerOrder:{so.Id:N}:Confirmed", now);
        }
    }
}
