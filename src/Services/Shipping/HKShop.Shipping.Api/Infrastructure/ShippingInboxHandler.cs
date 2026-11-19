using System.Text.Json;
using HKShop.Contracts.Events;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Messaging;
using HKShop.Shipping.Api.Application;
using HKShop.Shipping.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Shipping.Api.Infrastructure;

public sealed class ShippingInboxHandler(TimeProvider time) : IIntegrationEventHandler<ShippingDbContext>
{
    public async Task HandleAsync(ShippingDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case nameof(SellerOrderPlaced):
                await OnSellerOrderPlacedAsync(db, envelope.Payload.Deserialize<SellerOrderPlaced>()!, ct);
                break;

            case nameof(SellerOrderPacked):
                await OnSellerOrderPackedAsync(db, envelope.Payload.Deserialize<SellerOrderPacked>()!, ct);
                break;

            case nameof(OrderCancelled):
                await OnOrderCancelledAsync(db, envelope.Payload.Deserialize<OrderCancelled>()!, ct);
                break;

            case "SellerOrderCancelled":
                await OnSellerOrderCancelledAsync(db, envelope.Payload, ct);
                break;

            case nameof(PaymentCaptured):
                await OnPaymentCapturedAsync(db, envelope.Payload.Deserialize<PaymentCaptured>()!, ct);
                break;

            case nameof(SellerLocationChanged):
                await OnSellerLocationChangedAsync(db, envelope.Payload.Deserialize<SellerLocationChanged>()!, ct);
                break;

            case nameof(SellerMembershipChanged):
                await OnSellerMembershipChangedAsync(db, envelope.Payload.Deserialize<SellerMembershipChanged>()!, ct);
                break;

            case nameof(UserRolesChanged):
                await OnUserRolesChangedAsync(db, envelope.Payload.Deserialize<UserRolesChanged>()!, ct);
                break;

            case nameof(UserDisabled):
                await OnUserDisabledAsync(db, envelope.Payload.Deserialize<UserDisabled>()!, ct);
                break;
        }
    }

    private static async Task OnSellerOrderPlacedAsync(ShippingDbContext db, SellerOrderPlaced evt, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == evt.SellerOrderId, ct);
        if (snapshot is null)
        {
            snapshot = new ShippingOrderSnapshot { SellerOrderId = evt.SellerOrderId };
            db.OrderSnapshots.Add(snapshot);
        }

        snapshot.OrderId = evt.OrderId;
        snapshot.SellerId = evt.SellerId;
        snapshot.SellerOrderNumber = evt.SellerOrderNumber;
        if (!snapshot.CancelledBeforePlacement && !snapshot.PendingPackedAt.HasValue)
            snapshot.SellerOrderStatus = evt.Status;
        snapshot.TotalVnd = evt.TotalVnd;
        snapshot.ShippingVnd = evt.ShippingVnd;
        snapshot.PaymentMethod = evt.PaymentMethod;
        snapshot.PaymentStatus = evt.PaymentStatus;
        snapshot.Recipient = evt.Recipient;
        snapshot.Phone = evt.Phone;
        snapshot.ShippingAddress = evt.ShippingAddress;

        if (evt.Parcel is not null)
        {
            var existing = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == evt.SellerOrderId, ct);
            if (existing is null)
                db.TransportParcels.Add(ShippingOrderLifecycle.CreateParcelFromInput(evt.Parcel, snapshot));
        }
    }

    private async Task OnSellerOrderPackedAsync(ShippingDbContext db, SellerOrderPacked evt, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == evt.SellerOrderId, ct);
        if (snapshot is null)
        {
            snapshot = new ShippingOrderSnapshot
            {
                SellerOrderId = evt.SellerOrderId,
                OrderId = evt.OrderId,
                SellerId = evt.SellerId,
                SellerOrderNumber = evt.SellerOrderNumber,
                SellerOrderStatus = OrderStatus.Packing,
                PendingPackedAt = evt.PackedAt
            };
            db.OrderSnapshots.Add(snapshot);
        }
        else if (!snapshot.CancelledBeforePlacement)
        {
            snapshot.SellerOrderStatus = OrderStatus.Packing;
            snapshot.PendingPackedAt ??= evt.PackedAt;
        }

        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == evt.SellerOrderId, ct);
        if (parcel is null || parcel.Stage == TransportStage.Cancelled || parcel.PackedAt.HasValue)
            return;

        if (parcel.Stage == TransportStage.Packing)
        {
            parcel.PackedAt = evt.PackedAt;
            parcel.PickupDueAt = evt.PackedAt.AddHours(48);
            parcel.Stage = TransportStage.Packed;
            db.AddAuditEntry(null, "Transport parcel packed", evt.SellerOrderNumber, time.GetUtcNow(), sellerId: evt.SellerId);
        }
    }

    private async Task OnOrderCancelledAsync(ShippingDbContext db, OrderCancelled evt, CancellationToken ct)
    {
        var now = evt.CancelledAt != default ? evt.CancelledAt : time.GetUtcNow();
        foreach (var sellerOrderId in evt.SellerOrderIds)
            await CancelSellerOrderParcelAsync(db, sellerOrderId, evt.OrderId, now, ct);

        var snapshots = await db.OrderSnapshots.Where(s => s.OrderId == evt.OrderId).ToListAsync(ct);
        foreach (var snap in snapshots)
            await CancelSellerOrderParcelAsync(db, snap.SellerOrderId, evt.OrderId, now, ct);
    }

    private async Task OnSellerOrderCancelledAsync(ShippingDbContext db, JsonElement payload, CancellationToken ct)
    {
        if (!payload.TryGetProperty("SellerOrderId", out var idProp) || !idProp.TryGetGuid(out var sellerOrderId))
            return;
        var orderId = payload.TryGetProperty("OrderId", out var oProp) && oProp.TryGetGuid(out var oid) ? oid : Guid.Empty;
        await CancelSellerOrderParcelAsync(db, sellerOrderId, orderId, time.GetUtcNow(), ct);
    }

    private static async Task CancelSellerOrderParcelAsync(
        ShippingDbContext db, Guid sellerOrderId, Guid orderId, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId, ct);
        if (snapshot is null)
        {
            snapshot = new ShippingOrderSnapshot
            {
                SellerOrderId = sellerOrderId,
                OrderId = orderId,
                SellerOrderStatus = OrderStatus.Cancelled,
                CancelledBeforePlacement = true
            };
            db.OrderSnapshots.Add(snapshot);
        }
        else
        {
            snapshot.CancelledBeforePlacement = true;
            snapshot.SellerOrderStatus = OrderStatus.Cancelled;
        }

        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct);
        if (parcel is null || parcel.Stage == TransportStage.Cancelled)
            return;

        parcel.Stage = TransportStage.Cancelled;
        parcel.DeliveryCode = "";
        parcel.NextDispatchAt = null;
        parcel.AcknowledgeBy = null;
        var assignment = await db.TransportAssignments
            .FirstOrDefaultAsync(a => a.SellerOrderId == sellerOrderId && a.EndedAt == null, ct);
        if (assignment is not null)
        {
            assignment.EndedAt = now;
            assignment.Outcome = "Cancelled";
        }
    }

    private static async Task OnPaymentCapturedAsync(ShippingDbContext db, PaymentCaptured evt, CancellationToken ct)
    {
        var snapshots = await db.OrderSnapshots.Where(s => s.OrderId == evt.OrderId).ToListAsync(ct);
        foreach (var s in snapshots)
            s.PaymentStatus = PaymentStatus.Paid;
    }

    private static async Task OnSellerLocationChangedAsync(ShippingDbContext db, SellerLocationChanged evt, CancellationToken ct)
    {
        var loc = await db.SellerLocations.FirstOrDefaultAsync(l => l.LocationId == evt.LocationId, ct);
        if (loc is null)
        {
            loc = new ShippingSellerLocation { LocationId = evt.LocationId };
            db.SellerLocations.Add(loc);
        }
        loc.SellerId = evt.SellerId;
        loc.Name = evt.Name;
        loc.PickupAddress = evt.PickupAddress;
        loc.Latitude = evt.Latitude;
        loc.Longitude = evt.Longitude;
        loc.Active = evt.Active;
    }

    private static async Task OnSellerMembershipChangedAsync(ShippingDbContext db, SellerMembershipChanged evt, CancellationToken ct)
    {
        var row = await db.SellerMemberships.FirstOrDefaultAsync(m => m.SellerId == evt.SellerId && m.UserId == evt.UserId, ct);
        if (row is null)
        {
            row = new ShippingSellerMembership { SellerId = evt.SellerId, UserId = evt.UserId };
            db.SellerMemberships.Add(row);
        }
        row.Role = evt.Role;
        row.Status = evt.Status;
    }

    private static async Task OnUserRolesChangedAsync(ShippingDbContext db, UserRolesChanged evt, CancellationToken ct)
    {
        var shipper = await db.TransportShippers.FirstOrDefaultAsync(s => s.UserId == evt.UserId, ct);
        if (shipper is null)
        {
            if (!evt.ActiveShipper) return;
            db.TransportShippers.Add(new TransportShipper { UserId = evt.UserId, Email = evt.Email, Active = true });
            return;
        }
        shipper.Email = evt.Email;
        shipper.Active = evt.ActiveShipper;
        if (!evt.ActiveShipper) shipper.OnDuty = false;
    }

    private static async Task OnUserDisabledAsync(ShippingDbContext db, UserDisabled evt, CancellationToken ct)
    {
        var shipper = await db.TransportShippers.FirstOrDefaultAsync(s => s.UserId == evt.UserId, ct);
        if (shipper is not null)
        {
            shipper.Active = false;
            shipper.OnDuty = false;
        }
    }
}
