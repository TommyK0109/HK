using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Messaging;
using HKShop.Shipping.Api.Domain;
using HKShop.Shipping.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Shipping.Api.Application;

public sealed class ShippingOrderLifecycle(
    IDbContextFactory<ShippingDbContext> factory,
    TransportSettings settings,
    ITransportRouting routing,
    TimeProvider time)
{
    public bool Enabled => settings.Enabled;

    public static string DoorReference(Guid id) => $"HKD{id:N}".ToUpperInvariant();
    public static string RemittanceReference(Guid id) => $"HKR{id:N}".ToUpperInvariant();
    public static string PickupCode(Guid assignmentId) => assignmentId.ToString("N")[..8].ToUpperInvariant();

    public async Task<IReadOnlyList<TransportRoute>> QuoteRoutesAsync(
        TransportPoint destination,
        IReadOnlyList<SellerRouteOrigin> origins,
        IReadOnlyList<TransportRoute>? acceptedRoutes,
        bool quoteNewRoutes,
        CancellationToken ct)
    {
        var routes = new List<TransportRoute>(origins.Count);
        foreach (var sellerOrigin in origins)
        {
            var origin = TransportPoint.Require(sellerOrigin.Latitude, sellerOrigin.Longitude, "the storage location");
            long meters;
            if (quoteNewRoutes)
            {
                meters = await routing.Distance(origin, destination, ct)
                    ?? throw new ShopRuleException("No road route was found from storage to your address. Check your delivery pin.");
            }
            else
            {
                var saved = acceptedRoutes?.SingleOrDefault(r => r.SellerId == sellerOrigin.SellerId);
                if (saved is null || saved.LocationId != sellerOrigin.LocationId ||
                    saved.PickupAddress != sellerOrigin.PickupAddress ||
                    saved.Origin != origin || saved.Destination != destination)
                    throw new ShopRuleException("The delivery route or pickup location changed. Please review your order again.");
                meters = saved.DistanceMeters;
            }
            routes.Add(new(sellerOrigin.SellerId, sellerOrigin.LocationId, sellerOrigin.PickupAddress, origin, destination, meters));
        }
        return routes;
    }

    public async Task<Dictionary<Guid, TransportParcelSummary>> RegisterParcelsAsync(
        IReadOnlyList<NewTransportParcel> parcels,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var summaries = new Dictionary<Guid, TransportParcelSummary>(parcels.Count);
        foreach (var item in parcels)
        {
            var existing = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == item.SellerOrderId, ct);
            if (existing is null)
            {
                existing = CreateParcelFromInput(item, await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == item.SellerOrderId, ct));
                db.TransportParcels.Add(existing);
            }
            summaries[item.SellerOrderId] = existing.ToSummary();
        }
        await db.SaveChangesAsync(ct);
        return summaries;
    }

    public static TransportParcel CreateParcelFromInput(NewTransportParcel item, ShippingOrderSnapshot? snapshot)
    {
        var parcel = new TransportParcel
        {
            SellerOrderId = item.SellerOrderId,
            Stage = TransportStage.Packing,
            LocationId = item.LocationId,
            PickupAddress = item.PickupAddress,
            PickupLatitude = item.Origin.Latitude,
            PickupLongitude = item.Origin.Longitude,
            DestinationLatitude = item.Destination.Latitude,
            DestinationLongitude = item.Destination.Longitude,
            DistanceMeters = item.DistanceMeters,
            DeliveryCode = item.DeliveryCode
        };

        if (snapshot is { CancelledBeforePlacement: true })
        {
            parcel.Stage = TransportStage.Cancelled;
            parcel.DeliveryCode = "";
        }
        else if (snapshot is { PendingPackedAt: DateTimeOffset packedAt })
        {
            parcel.PackedAt = packedAt;
            parcel.PickupDueAt = packedAt.AddHours(48);
            parcel.Stage = TransportStage.Packed;
        }

        return parcel;
    }

    public async Task<Dictionary<Guid, TransportParcelSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> sellerOrderIds,
        bool redactDeliveryCode,
        CancellationToken ct)
    {
        if (sellerOrderIds.Count == 0)
            return new Dictionary<Guid, TransportParcelSummary>();

        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.TransportParcels.AsNoTracking()
            .Where(p => sellerOrderIds.Contains(p.SellerOrderId))
            .ToListAsync(ct);
        return rows.ToDictionary(p => p.SellerOrderId, p => p.ToSummary(redactDeliveryCode));
    }

    public async Task<SellerLocationSummary?> GetSellerLocationAsync(ShopActor actor, long sellerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFulfillmentAccessAsync(db, actor, sellerId, ct);
        var loc = await db.SellerLocations.AsNoTracking().FirstOrDefaultAsync(l => l.SellerId == sellerId && l.Active, ct);
        return loc is null
            ? null
            : new SellerLocationSummary(loc.LocationId, loc.SellerId, loc.Name, loc.PickupAddress, loc.Latitude, loc.Longitude, loc.Active);
    }

    public async Task SetSellerLocationAsync(ShopActor actor, long sellerId, string address, double latitude, double longitude, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFulfillmentAccessAsync(db, actor, sellerId, ct);
        var pin = TransportPoint.Require(latitude, longitude, "pickup");
        var normalizedAddress = ShopRules.Text(address, 300, "Pickup address");
        var loc = await db.SellerLocations.FirstOrDefaultAsync(l => l.SellerId == sellerId && l.Active, ct);
        if (loc is null)
        {
            loc = new ShippingSellerLocation
            {
                LocationId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SellerId = sellerId,
                Name = "Main warehouse",
                Active = true
            };
            db.SellerLocations.Add(loc);
        }

        loc.PickupAddress = normalizedAddress;
        loc.Latitude = pin.Latitude;
        loc.Longitude = pin.Longitude;
        var now = time.GetUtcNow();
        db.AddAuditEntry(actor.UserId, "Transport pickup location updated", loc.LocationId.ToString(), now, sellerId: sellerId);
        db.EnqueueOutboxEvent("SellerLocation", loc.LocationId.ToString(), nameof(SellerLocationChanged),
            new SellerLocationChanged(sellerId, loc.LocationId, loc.Name, normalizedAddress, pin.Latitude, pin.Longitude, true, now),
            $"ShippingLocation:{loc.LocationId}:{now.ToUnixTimeMilliseconds()}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> MarkPackedAsync(ShopActor actor, long sellerId, Guid sellerOrderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFulfillmentAccessAsync(db, actor, sellerId, ct);
        var snapshot = await RequireOrderSnapshotAsync(db, sellerOrderId, sellerId, ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");

        if (!parcel.PackedAt.HasValue)
        {
            if (parcel.Stage != TransportStage.Packing)
                throw new ShopRuleException("Start packing before marking the parcel packed.");
            var now = time.GetUtcNow();
            parcel.PackedAt = now;
            parcel.PickupDueAt = now.AddHours(48);
            parcel.Stage = TransportStage.Packed;
            db.AddAuditEntry(actor.UserId, "Transport parcel packed", snapshot.SellerOrderNumber, now, sellerId: snapshot.SellerId);
            await db.SaveChangesAsync(ct);
        }

        return snapshot.OrderId;
    }

    public async Task<Guid> RequestPickupAsync(ShopActor actor, long sellerId, Guid sellerOrderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFulfillmentAccessAsync(db, actor, sellerId, ct);
        var snapshot = await RequireOrderSnapshotAsync(db, sellerOrderId, sellerId, ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");

        if (!(parcel.RequestedAt.HasValue && parcel.Stage != TransportStage.Cancelled))
        {
            var now = time.GetUtcNow();
            if (parcel.Stage != TransportStage.Packed || parcel.PickupDueAt <= now)
                throw new ShopRuleException("This parcel is outside its pickup request window.");
            parcel.RequestedAt = now;
            parcel.NextDispatchAt = now;
            parcel.Stage = TransportStage.WaitingForShipper;
            db.AddAuditEntry(actor.UserId, "Transport pickup requested", snapshot.SellerOrderNumber, now, sellerId: snapshot.SellerId);
            await db.SaveChangesAsync(ct);
        }

        return snapshot.OrderId;
    }

    public async Task<Guid> ConfirmPickupAsync(ShopActor actor, long sellerId, Guid sellerOrderId, string code, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireSellerFulfillmentAccessAsync(db, actor, sellerId, ct);
        var snapshot = await RequireOrderSnapshotAsync(db, sellerOrderId, sellerId, ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");

        if (!parcel.PickedUpAt.HasValue)
        {
            if (parcel.Stage != TransportStage.Acknowledged)
                throw new ShopRuleException("The assigned shipper must acknowledge this pickup first.");
            var assignment = await db.TransportAssignments.FirstOrDefaultAsync(a => a.SellerOrderId == sellerOrderId && a.EndedAt == null, ct);
            if (assignment is null || !string.Equals(PickupCode(assignment.Id), code?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ShopRuleException("Enter the pickup code shown by the assigned HKShop shipper.");

            var now = time.GetUtcNow();
            parcel.Stage = TransportStage.PickedUp;
            parcel.PickedUpAt = now;
            snapshot.SellerOrderStatus = OrderStatus.Shipped;
            db.EnqueueOutboxEvent("TransportParcel", sellerOrderId.ToString("D"), nameof(ParcelPickedUp),
                new ParcelPickedUp(sellerOrderId, snapshot.OrderId, snapshot.SellerId, snapshot.SellerOrderNumber,
                    parcel.ShipperId ?? "", now),
                $"ParcelPickedUp:{sellerOrderId:N}", now);
            await db.SaveChangesAsync(ct);
        }

        return snapshot.OrderId;
    }

    public async Task CancelParcelAsync(Guid sellerOrderId, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId, ct);
        if (snapshot is not null)
        {
            snapshot.CancelledBeforePlacement = true;
            snapshot.SellerOrderStatus = OrderStatus.Cancelled;
        }
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct);
        if (parcel is not null && parcel.Stage != TransportStage.Cancelled)
        {
            parcel.Stage = TransportStage.Cancelled;
            parcel.DeliveryCode = "";
            parcel.NextDispatchAt = null;
            parcel.AcknowledgeBy = null;
            await EndAssignmentAsync(db, parcel, now, "Cancelled", ct);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> HasUnverifiedCashRemittanceAsync(Guid sellerOrderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcel = await db.TransportParcels.AsNoTracking().FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct);
        return parcel is not null && parcel.DoorPayment == DoorPayment.Cash && parcel.FundsVerifiedAt is null;
    }

    public async Task CompleteReturnAsync(Guid sellerOrderId, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct);
        if (parcel is null) return;
        parcel.Stage = TransportStage.Returned;
        parcel.DeliveryCode = "";
        await EndAssignmentAsync(db, parcel, now, "Returned", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ConfigureShipperAsync(ShopActor actor, string userId, string email, bool active, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TransportShippers.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (row is null)
        {
            if (!active) return;
            row = new TransportShipper { UserId = userId };
            db.TransportShippers.Add(row);
        }
        if (!active && await db.TransportAssignments.AnyAsync(a => a.ShipperId == userId && a.EndedAt == null, ct))
            throw new ShopRuleException("Resolve the shipper's active assignment before disabling their account.");

        row.Active = active;
        row.Email = email;
        if (!active) row.OnDuty = false;
        db.AddAuditEntry(actor.UserId, active ? "Transport shipper enabled" : "Transport shipper disabled", userId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdatePresenceAsync(ShopActor actor, bool onDuty, double? latitude, double? longitude, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var shipper = await RequireShipperAsync(db, actor, ct);
        shipper.OnDuty = onDuty;
        if (onDuty)
        {
            var pin = TransportPoint.Require(latitude, longitude, "shipper availability");
            shipper.Latitude = pin.Latitude;
            shipper.Longitude = pin.Longitude;
            shipper.LocatedAt = time.GetUtcNow();
        }
        else
        {
            shipper.Latitude = null;
            shipper.Longitude = null;
            shipper.LocatedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RespondAsync(ShopActor actor, Guid sellerOrderId, Guid assignmentId, bool accept, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (_, parcel) = await AssignedParcelAsync(db, actor, sellerOrderId, ct);
        var now = time.GetUtcNow();
        var matching = await db.TransportAssignments.AnyAsync(a =>
            a.Id == assignmentId && a.SellerOrderId == sellerOrderId && a.ShipperId == actor.UserId && a.EndedAt == null, ct);
        if (!matching) throw new ShopRuleException("This assignment is no longer available.");
        if (accept && parcel.Stage == TransportStage.Acknowledged) return;
        if (parcel.Stage != TransportStage.Assigned || parcel.AcknowledgeBy <= now)
            throw new ShopRuleException("This assignment is no longer available.");

        if (accept)
        {
            parcel.Stage = TransportStage.Acknowledged;
            parcel.AcknowledgedAt = now;
            parcel.AcknowledgeBy = null;
        }
        else
        {
            await RequeueAsync(db, parcel, now, "Declined", ct);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeliverAsync(ShopActor actor, Guid sellerOrderId, string code, DoorPayment? method, decimal? cash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (snapshot, parcel) = await AssignedParcelAsync(db, actor, sellerOrderId, ct);
        if (parcel.Stage == TransportStage.Delivered) return true;
        if (parcel.Stage != TransportStage.PickedUp)
            throw new ShopRuleException("Pick up the parcel first.");
        if (parcel.FailedDeliveryCodes >= 5)
            throw new ShopRuleException("Ask HKShop dispatch to reset delivery confirmation.");
        if (code != parcel.DeliveryCode)
        {
            parcel.FailedDeliveryCodes++;
            await db.SaveChangesAsync(ct);
            return false;
        }

        var now = time.GetUtcNow();
        if (snapshot.PaymentMethod == PaymentMethod.TransportAtDelivery)
        {
            if (method == DoorPayment.Cash && parcel.FundsVerifiedAt is null && cash == snapshot.TotalVnd)
            {
                parcel.DoorPayment = method;
                parcel.CashCollectedVnd = cash.Value;
                if (snapshot.TotalVnd == snapshot.ShippingVnd)
                {
                    parcel.FundsVerifiedAt = now;
                    db.EnqueueOutboxEvent("TransportParcel", sellerOrderId.ToString("D"), nameof(DoorPaymentVerified),
                        new DoorPaymentVerified(sellerOrderId, snapshot.OrderId, snapshot.SellerId,
                            snapshot.TotalVnd, snapshot.ShippingVnd, null, false, now),
                        $"DoorPaymentVerified:{sellerOrderId:N}", now);
                }
            }
            else if (method != DoorPayment.BankTransfer || parcel.DoorPayment != method || parcel.FundsVerifiedAt is null)
            {
                throw new ShopRuleException("Record the exact cash collected or wait for HKShop to verify the bank transfer.");
            }
        }
        else if (snapshot.PaymentStatus != PaymentStatus.Paid)
        {
            throw new ShopRuleException("The online payment has not been confirmed.");
        }

        parcel.Stage = TransportStage.Delivered;
        parcel.DeliveryCode = "";
        parcel.ShipperFeeVnd = snapshot.ShippingVnd;
        if (parcel.DoorPayment == DoorPayment.Cash)
            parcel.ShipperPaidAt = now;
        snapshot.SellerOrderStatus = OrderStatus.Delivered;

        db.EnqueueOutboxEvent("TransportParcel", sellerOrderId.ToString("D"), nameof(ParcelDelivered),
            new ParcelDelivered(sellerOrderId, snapshot.OrderId, snapshot.SellerId, actor.UserId,
                parcel.DoorPayment, parcel.FundsVerifiedAt.HasValue, parcel.ShipperFeeVnd, now),
            $"ParcelDelivered:{sellerOrderId:N}", now);
        await EndAssignmentAsync(db, parcel, now, "Delivered", ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReturnToSenderAsync(ShopActor actor, Guid sellerOrderId, string issue, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (snapshot, parcel) = await AssignedParcelAsync(db, actor, sellerOrderId, ct);
        if (parcel.Stage == TransportStage.Returning) return;
        if (parcel.Stage != TransportStage.PickedUp)
            throw new ShopRuleException("Only a picked-up parcel can be returned.");
        var normalizedIssue = ShopRules.Text(issue, 250, "Delivery issue");
        var now = time.GetUtcNow();
        parcel.Issue = normalizedIssue;
        parcel.Stage = TransportStage.Returning;
        db.EnqueueOutboxEvent("TransportParcel", sellerOrderId.ToString("D"), nameof(ParcelReturnedToSender),
            new ParcelReturnedToSender(sellerOrderId, snapshot.OrderId, snapshot.SellerId, normalizedIssue, now),
            $"ParcelReturnedToSender:{sellerOrderId:N}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReassignAsync(ShopActor actor, Guid sellerOrderId, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");
        if (parcel.Stage is not (TransportStage.Assigned or TransportStage.Acknowledged))
            throw new ShopRuleException("Only an assignment awaiting pickup can be reassigned.");
        var now = time.GetUtcNow();
        await RequeueAsync(db, parcel, now, "Reassigned", ct);
        db.AddAuditEntry(actor.UserId, "Transport pickup reassigned", sellerOrderId.ToString(), now);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetDeliveryConfirmationAsync(ShopActor actor, Guid sellerOrderId, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");
        if (parcel.Stage != TransportStage.PickedUp)
            throw new ShopRuleException("The parcel is not out for delivery.");
        parcel.FailedDeliveryCodes = 0;
        db.AddAuditEntry(actor.UserId, "Transport delivery code retries reset", sellerOrderId.ToString(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordBankReceiptAsync(string transactionId, string reference, decimal amount, CancellationToken ct)
    {
        transactionId = ShopRules.Text(transactionId, 100, "Bank transaction ID");
        reference = ShopRules.Text(reference, 100, "Transfer reference", false).ToUpperInvariant();
        ShopRules.Vnd(amount);

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.TransportBankReceipts.AnyAsync(r => r.TransactionId == transactionId, ct))
            return;

        var now = time.GetUtcNow();
        var receipt = new TransportBankReceipt
        {
            TransactionId = transactionId,
            Reference = reference,
            AmountVnd = amount,
            ReceivedAt = now,
            Issue = "The bank reference does not identify a HKShop parcel."
        };
        db.TransportBankReceipts.Add(receipt);

        if (reference.Length == 35 && Guid.TryParseExact(reference[3..], "N", out var id))
        {
            var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == id, ct);
            if (parcel is not null)
                await ApplyBankReceiptAsync(db, receipt, parcel, reference, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveBankReceiptAsync(ShopActor actor, string transactionId, string? reference, string? resolution, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        var receipt = await db.TransportBankReceipts.FirstOrDefaultAsync(r => r.TransactionId == transactionId, ct)
            ?? throw new ShopRuleException("Bank transaction not found.");
        if (receipt.Issue.Length == 0 || receipt.ResolvedAt.HasValue) return;

        var now = time.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var corrected = reference.Trim().ToUpperInvariant();
            if (corrected.Length != 35 || !Guid.TryParseExact(corrected[3..], "N", out var id))
                throw new ShopRuleException("Enter the parcel's buyer transfer or remittance reference.");
            var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == id, ct)
                ?? throw new ShopRuleException("Transport parcel not found.");
            await ApplyBankReceiptAsync(db, receipt, parcel, corrected, now, ct);
            if (receipt.Issue.Length != 0) throw new ShopRuleException(receipt.Issue);
            receipt.Resolution = $"Matched to {corrected}";
        }
        else
        {
            receipt.Resolution = ShopRules.Text(resolution, 250, "Verified external resolution");
        }

        receipt.ResolvedAt = now;
        db.AddAuditEntry(actor.UserId, "Transport bank exception reconciled", transactionId, now, receipt.Resolution);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordShipperPayoutAsync(ShopActor actor, Guid sellerOrderId, string reference, CancellationToken ct)
    {
        actor.RequireAdmin();
        reference = ShopRules.Text(reference, 100, "Payout reference");
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");
        var snapshot = await db.OrderSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId, ct);

        if (parcel.ShipperPayoutReference == reference) return;
        if (parcel.Stage != TransportStage.Delivered || parcel.ShipperFeeVnd == 0 || parcel.ShipperPaidAt is not null ||
            await db.TransportParcels.AnyAsync(p => p.ShipperPayoutReference == reference, ct))
            throw new ShopRuleException("This shipper payout cannot be applied.");

        var now = time.GetUtcNow();
        parcel.ShipperPayoutReference = reference;
        parcel.ShipperPaidAt = now;
        db.AddAuditEntry(actor.UserId, "Transport shipper payout reconciled", sellerOrderId.ToString(), now, reference);
        db.EnqueueOutboxEvent("TransportParcel", sellerOrderId.ToString("D"), nameof(ShipperFeeDue),
            new ShipperFeeDue(sellerOrderId, snapshot?.OrderId ?? Guid.Empty, parcel.ShipperFeeVnd, reference, actor.UserId, now),
            $"ShipperFeeDue:{sellerOrderId:N}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(ShopActor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var parcels = await db.TransportParcels.AsNoTracking()
            .Where(p =>
                p.Stage == TransportStage.WaitingForShipper || p.Stage == TransportStage.Assigned ||
                p.Stage == TransportStage.Acknowledged || p.Stage == TransportStage.Returning ||
                (p.Stage == TransportStage.PickedUp && p.FailedDeliveryCodes >= 5) ||
                (p.Stage == TransportStage.Delivered &&
                 ((p.DoorPayment == DoorPayment.Cash && p.FundsVerifiedAt == null) ||
                  (p.ShipperFeeVnd > 0 && p.ShipperPaidAt == null))))
            .OrderBy(p => p.RequestedAt)
            .Take(100)
            .ToListAsync(ct);

        var ids = parcels.Select(p => p.SellerOrderId).ToArray();
        var snapshots = await db.OrderSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.SellerOrderId))
            .ToDictionaryAsync(s => s.SellerOrderId, ct);
        var shipperIds = parcels.Where(p => p.ShipperId != null).Select(p => p.ShipperId!).Distinct().ToArray();
        var shippers = await db.TransportShippers.AsNoTracking()
            .Where(s => shipperIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.Email, ct);

        return parcels.Select(p =>
        {
            var s = snapshots.GetValueOrDefault(p.SellerOrderId);
            return new TransportOperationRow(
                p.ToSummary(redactDeliveryCode: true),
                s?.OrderId ?? Guid.Empty,
                s?.SellerOrderNumber ?? p.SellerOrderId.ToString("N"),
                "",
                "",
                "",
                s?.PaymentMethod ?? PaymentMethod.TransportAtDelivery,
                s?.TotalVnd ?? 0m,
                s?.ShippingVnd ?? p.ShipperFeeVnd,
                p.ShipperId is not null ? shippers.GetValueOrDefault(p.ShipperId) : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(ShopActor actor, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TransportBankReceipts.AsNoTracking()
            .Where(r => r.Issue != "" && r.ResolvedAt == null)
            .OrderByDescending(r => r.ReceivedAt)
            .Take(100)
            .Select(r => new TransportBankExceptionRow(
                r.TransactionId, r.Reference, r.AmountVnd, r.ReceivedAt, r.SellerOrderId, r.Issue, r.ResolvedAt, r.Resolution))
            .ToListAsync(ct);
    }

    public async Task<ShipperStateDto> ShipperStateAsync(ShopActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var shipper = await RequireShipperAsync(db, actor, ct);
        var assignment = await db.TransportAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ShipperId == actor.UserId && a.EndedAt == null, ct);
        TransportJobDto? activeJob = null;
        if (assignment is not null)
        {
            var parcel = await db.TransportParcels.AsNoTracking()
                .FirstOrDefaultAsync(p => p.SellerOrderId == assignment.SellerOrderId, ct);
            if (parcel is not null)
                activeJob = await BuildJobDtoAsync(db, parcel, parcel.PickedUpAt.HasValue, ct);
        }

        var remittanceParcels = await db.TransportParcels.AsNoTracking()
            .Where(p => p.ShipperId == actor.UserId && p.Stage == TransportStage.Delivered &&
                        p.DoorPayment == DoorPayment.Cash && p.FundsVerifiedAt == null)
            .OrderBy(p => p.ShipperPaidAt)
            .Take(50)
            .ToListAsync(ct);
        var remittances = new List<TransportJobDto>(remittanceParcels.Count);
        foreach (var parcel in remittanceParcels)
            remittances.Add(await BuildJobDtoAsync(db, parcel, false, ct));

        return new ShipperStateDto(shipper.OnDuty, activeJob, assignment?.Id, remittances);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var expired = await db.TransportParcels
                .Where(p => (p.Stage == TransportStage.Packed && p.PickupDueAt <= now) ||
                            (p.Stage == TransportStage.Assigned && p.AcknowledgeBy <= now))
                .OrderBy(p => p.PackedAt)
                .Take(100)
                .ToListAsync(ct);

            foreach (var parcel in expired)
            {
                if (parcel.Stage == TransportStage.Assigned && parcel.AcknowledgeBy <= now)
                    await RequeueAsync(db, parcel, now, "Timed out", ct);

                if (parcel.Stage == TransportStage.Packed && parcel.PickupDueAt <= now)
                {
                    parcel.Stage = TransportStage.Cancelled;
                    parcel.DeliveryCode = "";
                    parcel.NextDispatchAt = null;
                    parcel.AcknowledgeBy = null;
                    await EndAssignmentAsync(db, parcel, now, "Cancelled", ct);
                    var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == parcel.SellerOrderId, ct);
                    if (snapshot is not null)
                        snapshot.SellerOrderStatus = OrderStatus.Cancelled;
                    db.EnqueueOutboxEvent("TransportParcel", parcel.SellerOrderId.ToString("D"), nameof(PickupDeadlineExpired),
                        new PickupDeadlineExpired(parcel.SellerOrderId, snapshot?.OrderId ?? Guid.Empty, snapshot?.SellerId ?? 0, now),
                        $"PickupDeadlineExpired:{parcel.SellerOrderId:N}", now);
                }
            }
            await db.SaveChangesAsync(ct);
        }

        List<TransportParcel> waiting;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            waiting = await db.TransportParcels.AsNoTracking()
                .Where(p => p.Stage == TransportStage.WaitingForShipper && p.NextDispatchAt <= now)
                .OrderBy(p => p.RequestedAt)
                .ThenBy(p => p.SellerOrderId)
                .Take(25)
                .ToListAsync(ct);
        }

        foreach (var parcel in waiting)
            await AssignAsync(parcel, ct);
    }

    private async Task AssignAsync(TransportParcel waiting, CancellationToken ct)
    {
        var id = waiting.SellerOrderId;
        var pickup = new TransportPoint(waiting.PickupLatitude, waiting.PickupLongitude);
        List<TransportShipper> candidates;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            candidates = await Eligible(db, id, time.GetUtcNow()).ToListAsync(ct);
        }

        var distances = new List<(TransportShipper Shipper, long Meters)>();
        foreach (var shipper in candidates)
        {
            var point = new TransportPoint(shipper.Latitude!.Value, shipper.Longitude!.Value);
            if (point.DistanceTo(pickup) > 10_000) continue;
            var distance = await routing.Distance(point, pickup, ct);
            if (distance is <= 10_000) distances.Add((shipper, distance.Value));
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == id, ct);
            if (parcel is null) return;
            var now = time.GetUtcNow();
            if (parcel.Stage != TransportStage.WaitingForShipper || parcel.NextDispatchAt > now) return;

            foreach (var candidate in distances.OrderBy(d => d.Meters).ThenBy(d => d.Shipper.UserId, StringComparer.Ordinal))
            {
                var stillAvailable = await Eligible(db, id, now)
                    .FirstOrDefaultAsync(s => s.UserId == candidate.Shipper.UserId, ct);
                if (stillAvailable is null) continue;

                parcel.Stage = TransportStage.Assigned;
                parcel.ShipperId = stillAvailable.UserId;
                parcel.AcknowledgeBy = now.AddMinutes(10);
                parcel.NextDispatchAt = null;
                db.TransportAssignments.Add(new TransportAssignment
                {
                    SellerOrderId = id,
                    ShipperId = stillAvailable.UserId,
                    AssignedAt = now
                });
                await db.SaveChangesAsync(ct);
                return;
            }

            parcel.NextDispatchAt = now.AddMinutes(1);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ApplyBankReceiptAsync(
        ShippingDbContext db,
        TransportBankReceipt receipt,
        TransportParcel parcel,
        string reference,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var id = parcel.SellerOrderId;
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == id, ct)
            ?? throw new ShopRuleException("Seller order snapshot not found.");

        var isDoor = reference == DoorReference(id);
        var isRemittance = reference == RemittanceReference(id);
        receipt.SellerOrderId = id;
        if (snapshot.PaymentMethod != PaymentMethod.TransportAtDelivery || (!isDoor && !isRemittance)) return;

        receipt.Issue = "The transfer does not match the amount or stage expected for this parcel.";
        if (parcel.FundsVerifiedAt is not null)
        {
            receipt.Issue = "Funds have already been verified for this parcel.";
            return;
        }

        if (isDoor && parcel.Stage == TransportStage.PickedUp && parcel.DoorPayment is null && receipt.AmountVnd == snapshot.TotalVnd)
            parcel.DoorPayment = DoorPayment.BankTransfer;
        else if (!isRemittance || parcel.Stage != TransportStage.Delivered || parcel.DoorPayment != DoorPayment.Cash ||
                 receipt.AmountVnd != snapshot.TotalVnd - snapshot.ShippingVnd)
            return;

        parcel.FundsVerifiedAt = now;
        receipt.Issue = "";
        db.EnqueueOutboxEvent("TransportParcel", id.ToString("D"), nameof(DoorPaymentVerified),
            new DoorPaymentVerified(id, snapshot.OrderId, snapshot.SellerId, snapshot.TotalVnd, snapshot.ShippingVnd,
                $"BANK-{receipt.TransactionId}", isRemittance, now),
            $"DoorPaymentVerified:{id:N}:{receipt.TransactionId}", now);
    }

    private static IQueryable<TransportShipper> Eligible(ShippingDbContext db, Guid id, DateTimeOffset now)
    {
        var locationCutoff = now.AddMinutes(-2);
        var retryCutoff = now.AddMinutes(-30);
        return db.TransportShippers.Where(s =>
            s.Active && s.OnDuty && s.LocatedAt >= locationCutoff &&
            s.Latitude != null && s.Longitude != null &&
            !db.TransportAssignments.Any(a => a.ShipperId == s.UserId &&
                (a.EndedAt == null || (a.SellerOrderId == id && a.EndedAt >= retryCutoff))));
    }

    private static async Task RequeueAsync(ShippingDbContext db, TransportParcel parcel, DateTimeOffset now, string outcome, CancellationToken ct)
    {
        await EndAssignmentAsync(db, parcel, now, outcome, ct);
        parcel.Stage = TransportStage.WaitingForShipper;
        parcel.NextDispatchAt = now;
        parcel.ShipperId = null;
        parcel.AcknowledgeBy = null;
        parcel.AcknowledgedAt = null;
    }

    private static async Task EndAssignmentAsync(ShippingDbContext db, TransportParcel parcel, DateTimeOffset now, string outcome, CancellationToken ct)
    {
        var assignment = await db.TransportAssignments
            .FirstOrDefaultAsync(a => a.SellerOrderId == parcel.SellerOrderId && a.EndedAt == null, ct);
        if (assignment is null) return;
        assignment.EndedAt = now;
        assignment.Outcome = outcome;
    }

    private async Task<TransportJobDto> BuildJobDtoAsync(ShippingDbContext db, TransportParcel parcel, bool revealDestination, CancellationToken ct)
    {
        var s = await db.OrderSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SellerOrderId == parcel.SellerOrderId, ct);
        var shipperEmail = parcel.ShipperId is null
            ? null
            : await db.TransportShippers.AsNoTracking()
                .Where(x => x.UserId == parcel.ShipperId)
                .Select(x => x.Email)
                .FirstOrDefaultAsync(ct);

        return new TransportJobDto(
            parcel.ToSummary(redactDeliveryCode: true),
            s?.OrderId ?? Guid.Empty,
            s?.SellerOrderNumber ?? parcel.SellerOrderId.ToString("N"),
            revealDestination ? s?.Recipient ?? "" : "",
            revealDestination ? s?.Phone ?? "" : "",
            revealDestination ? s?.ShippingAddress ?? "" : "",
            s?.PaymentMethod ?? PaymentMethod.TransportAtDelivery,
            s?.TotalVnd ?? 0m,
            s?.ShippingVnd ?? parcel.ShipperFeeVnd,
            shipperEmail);
    }

    private static async Task RequireSellerFulfillmentAccessAsync(ShippingDbContext db, ShopActor actor, long sellerId, CancellationToken ct)
    {
        if (actor.Admin || actor.Staff) return;
        if (string.IsNullOrEmpty(actor.UserId) || !actor.Verified || !actor.Mfa)
            throw new ShopRuleException("You don't have access to this seller.");
        var member = await db.SellerMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SellerId == sellerId && m.UserId == actor.UserId &&
                                      m.Status == SellerMembershipStatus.Active, ct);
        if (member is null || member.Role is not (SellerRole.Owner or SellerRole.Fulfillment))
            throw new ShopRuleException("You don't have access to this seller.");
    }

    private static async Task<ShippingOrderSnapshot> RequireOrderSnapshotAsync(ShippingDbContext db, Guid sellerOrderId, long sellerId, CancellationToken ct) =>
        await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId && s.SellerId == sellerId, ct)
            ?? throw new ShopRuleException("Seller order not found.");

    private static async Task<(ShippingOrderSnapshot Snapshot, TransportParcel Parcel)> AssignedParcelAsync(
        ShippingDbContext db, ShopActor actor, Guid sellerOrderId, CancellationToken ct)
    {
        await RequireShipperAsync(db, actor, ct);
        var snapshot = await db.OrderSnapshots.FirstOrDefaultAsync(s => s.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Seller order not found.");
        var parcel = await db.TransportParcels.FirstOrDefaultAsync(p => p.SellerOrderId == sellerOrderId, ct)
            ?? throw new ShopRuleException("Transport parcel not found.");
        if (parcel.ShipperId != actor.UserId)
            throw new ShopRuleException("The parcel is assigned to another shipper.");
        return (snapshot, parcel);
    }

    private static async Task<TransportShipper> RequireShipperAsync(ShippingDbContext db, ShopActor actor, CancellationToken ct)
    {
        if (!actor.Mfa) throw new ShopRuleException("Shippers must sign in with MFA.");
        return await db.TransportShippers.FirstOrDefaultAsync(s => s.UserId == actor.UserId && s.Active, ct)
            ?? throw new ShopRuleException("This account is not an active HKShop shipper.");
    }
}
