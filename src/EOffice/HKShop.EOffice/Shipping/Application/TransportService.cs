using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;

namespace HKShop.Application.Transportation;

public sealed record TransportJob(TransportParcel Parcel, Guid OrderId, string Number, string Recipient, string Phone,
    string Destination, PaymentMethod Method, decimal TotalVnd, decimal ShippingVnd, string? ShipperEmail);

public sealed class TransportService(IShopStore store, TimeProvider time, ITransportRouting routing, IShippingCommerceCallbackPort commerce)
{
    public static string DoorReference(Guid id) => $"HKD{id:N}".ToUpperInvariant();
    public static string RemittanceReference(Guid id) => $"HKR{id:N}".ToUpperInvariant();
    public static string PickupCode(Guid assignmentId) => assignmentId.ToString("N")[..8].ToUpperInvariant();

    public Task<int> SetLocation(ShopActor actor, long sellerId, string address, double latitude, double longitude) =>
        store.Write(async db =>
        {
            await commerce.SetSellerLocationAsync(db, actor, sellerId, address, latitude, longitude, time.GetUtcNow());
            return 0;
        });

    public Task<SellerLocationSummary?> Location(ShopActor actor, long sellerId) =>
        store.Write(db => commerce.GetSellerLocationAsync(db, actor, sellerId));

    public Task<int> ConfigureShipper(ShopActor actor, string userId, string email, bool active) =>
        store.Write(db => ConfigureShipper(db, actor, userId, email, active));

    internal async Task<int> ConfigureShipper(IShopSession db, ShopActor actor, string userId, string email, bool active)
    {
        actor.RequireAdmin();
        if (active) await commerce.RequireShipperEligibilityAsync(db, userId);
        var row = await db.First(db.Query<TransportShipper>().Where(s => s.UserId == userId));
        if (row is null)
        {
            if (!active) return 0;
            row = new TransportShipper { UserId = userId };
            db.Add(row);
        }
        if (!active && await db.Count(db.Query<TransportAssignment>().Where(a => a.ShipperId == userId && a.EndedAt == null)) != 0)
            throw new ShopRuleException("Resolve the shipper's active assignment before disabling their account.");
        row.Active = active;
        row.Email = email;
        if (!active) row.OnDuty = false;
        await commerce.AuditSellerTransportAsync(db, actor.UserId, active ? "Transport shipper enabled" : "Transport shipper disabled", userId, time.GetUtcNow());
        return 0;
    }

    public Task<int> UpdatePresence(ShopActor actor, bool onDuty, double? latitude, double? longitude) =>
        store.Write(async db =>
        {
            var shipper = await RequireShipper(db, actor);
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
            return 0;
        });

    public Task<Guid> MarkPacked(ShopActor actor, long sellerId, Guid id) =>
        SellerAction(actor, sellerId, id, async (db, child, parcel, now) =>
        {
            if (parcel.PackedAt.HasValue) return;
            if (child.SellerOrderStatus != OrderStatus.Packing || parcel.Stage != TransportStage.Packing)
                throw new ShopRuleException("Start packing before marking the parcel packed.");
            parcel.PackedAt = now;
            parcel.PickupDueAt = now.AddHours(48);
            parcel.Stage = TransportStage.Packed;
            await commerce.AuditSellerTransportAsync(db, actor.UserId, "Transport parcel packed", child.SellerOrderNumber, now, sellerId: child.SellerId);
        });

    public Task<Guid> RequestPickup(ShopActor actor, long sellerId, Guid id) =>
        SellerAction(actor, sellerId, id, async (db, child, parcel, now) =>
        {
            if (parcel.RequestedAt.HasValue && parcel.Stage != TransportStage.Cancelled) return;
            if (parcel.Stage != TransportStage.Packed || parcel.PickupDueAt <= now)
                throw new ShopRuleException("This parcel is outside its pickup request window.");
            parcel.RequestedAt = now;
            parcel.NextDispatchAt = now;
            parcel.Stage = TransportStage.WaitingForShipper;
            await commerce.AuditSellerTransportAsync(db, actor.UserId, "Transport pickup requested", child.SellerOrderNumber, now, sellerId: child.SellerId);
        });

    public Task<Guid> ConfirmPickup(ShopActor actor, long sellerId, Guid id, string code) =>
        SellerAction(actor, sellerId, id, async (db, child, parcel, now) =>
        {
            if (parcel.PickedUpAt.HasValue) return;
            if (parcel.Stage != TransportStage.Acknowledged)
                throw new ShopRuleException("The assigned shipper must acknowledge this pickup first.");
            var assignment = await db.First(db.Query<TransportAssignment>().Where(a => a.SellerOrderId == id && a.EndedAt == null));
            if (assignment is null || !string.Equals(PickupCode(assignment.Id), code?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ShopRuleException("Enter the pickup code shown by the assigned HKShop shipper.");
            parcel.Stage = TransportStage.PickedUp;
            parcel.PickedUpAt = now;
            await commerce.OnParcelPickedUpAsync(db, actor, id, now);
        });

    public Task<int> Respond(ShopActor actor, Guid id, Guid assignmentId, bool accept) =>
        store.Write(async db =>
        {
            var (_, parcel) = await AssignedParcel(db, actor, id);
            var now = time.GetUtcNow();
            var matching = await db.Count(db.Query<TransportAssignment>().Where(a => a.Id == assignmentId &&
                a.SellerOrderId == id && a.ShipperId == actor.UserId && a.EndedAt == null));
            if (matching == 0) throw new ShopRuleException("This assignment is no longer available.");
            if (accept && parcel.Stage == TransportStage.Acknowledged) return 0;
            if (parcel.Stage != TransportStage.Assigned || parcel.AcknowledgeBy <= now)
                throw new ShopRuleException("This assignment is no longer available.");
            if (accept)
            {
                parcel.Stage = TransportStage.Acknowledged;
                parcel.AcknowledgedAt = now;
                parcel.AcknowledgeBy = null;
            }
            else await Requeue(db, parcel, now, "Declined");
            return 0;
        });

    public Task<int> Reassign(ShopActor actor, Guid id)
    {
        actor.RequireStaff();
        return store.Write(async db =>
        {
            var (_, parcel) = await FindParcel(db, id);
            if (parcel.Stage is not (TransportStage.Assigned or TransportStage.Acknowledged))
                throw new ShopRuleException("Only an assignment awaiting pickup can be reassigned.");
            var now = time.GetUtcNow();
            await Requeue(db, parcel, now, "Reassigned");
            await commerce.AuditSellerTransportAsync(db, actor.UserId, "Transport pickup reassigned", id.ToString(), now);
            return 0;
        });
    }

    public Task<bool> Deliver(ShopActor actor, Guid id, string code, DoorPayment? method, decimal? cash) =>
        store.Write(async db =>
        {
            var (child, parcel) = await AssignedParcel(db, actor, id);
            if (parcel.Stage == TransportStage.Delivered) return true;
            if (parcel.Stage != TransportStage.PickedUp)
                throw new ShopRuleException("Pick up the parcel first.");
            if (parcel.FailedDeliveryCodes >= 5)
                throw new ShopRuleException("Ask HKShop dispatch to reset delivery confirmation.");
            if (code != parcel.DeliveryCode)
            {
                parcel.FailedDeliveryCodes++;
                return false;
            }
            var now = time.GetUtcNow();
            if (child.PaymentMethod == PaymentMethod.TransportAtDelivery)
            {
                if (method == DoorPayment.Cash && parcel.FundsVerifiedAt is null && cash == child.TotalVnd)
                {
                    parcel.DoorPayment = method;
                    parcel.CashCollectedVnd = cash.Value;
                    if (child.TotalVnd == child.ShippingVnd)
                    {
                        parcel.FundsVerifiedAt = now;
                        await commerce.OnDoorPaymentVerifiedAsync(db, id, null, postRetainedFee: false, now);
                    }
                }
                else if (method != DoorPayment.BankTransfer || parcel.DoorPayment != method || parcel.FundsVerifiedAt is null)
                    throw new ShopRuleException("Record the exact cash collected or wait for HKShop to verify the bank transfer.");
            }
            else if (child.PaymentStatus != PaymentStatus.Paid)
                throw new ShopRuleException("The online payment has not been confirmed.");

            parcel.Stage = TransportStage.Delivered;
            parcel.DeliveryCode = "";
            parcel.ShipperFeeVnd = child.ShippingVnd;
            if (parcel.DoorPayment == DoorPayment.Cash)
                parcel.ShipperPaidAt = now;
            await commerce.OnParcelDeliveredAsync(db, actor, id, parcel.DoorPayment, parcel.FundsVerifiedAt.HasValue, parcel.ShipperFeeVnd, now);
            await EndAssignment(db, parcel, now, "Delivered");
            return true;
        });

    public Task<int> ReturnToSender(ShopActor actor, Guid id, string issue) =>
        store.Write(async db =>
        {
            var (_, parcel) = await AssignedParcel(db, actor, id);
            if (parcel.Stage == TransportStage.Returning) return 0;
            if (parcel.Stage != TransportStage.PickedUp)
                throw new ShopRuleException("Only a picked-up parcel can be returned.");
            parcel.Issue = ShopRules.Text(issue, 250, "Delivery issue");
            parcel.Stage = TransportStage.Returning;
            await commerce.NotifyReturnToSenderAsync(db, id, time.GetUtcNow());
            return 0;
        });

    public Task<int> ResetDeliveryConfirmation(ShopActor actor, Guid id)
    {
        actor.RequireStaff();
        return store.Write(async db =>
        {
            var (_, parcel) = await FindParcel(db, id);
            if (parcel.Stage != TransportStage.PickedUp) throw new ShopRuleException("The parcel is not out for delivery.");
            parcel.FailedDeliveryCodes = 0;
            await commerce.AuditSellerTransportAsync(db, actor.UserId, "Transport delivery code retries reset", id.ToString(), time.GetUtcNow());
            return 0;
        });
    }

    public Task<int> RecordBankReceipt(string transactionId, string reference, decimal amount)
    {
        transactionId = ShopRules.Text(transactionId, 100, "Bank transaction ID");
        reference = ShopRules.Text(reference, 100, "Transfer reference", false).ToUpperInvariant();
        ShopRules.Vnd(amount);
        return store.Write(async db =>
        {
            if (await db.Count(db.Query<TransportBankReceipt>().Where(r => r.TransactionId == transactionId)) != 0) return 0;
            var now = time.GetUtcNow();
            var receipt = new TransportBankReceipt
            {
                TransactionId = transactionId, Reference = reference, AmountVnd = amount, ReceivedAt = now,
                Issue = "The bank reference does not identify a HKShop parcel."
            };
            db.Add(receipt);
            if (reference.Length != 35 || !Guid.TryParseExact(reference[3..], "N", out var id)) return 0;
            var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == id));
            if (parcel is null) return 0;
            await ApplyBankReceipt(db, receipt, parcel, reference, now);
            return 0;
        });
    }

    public Task<int> ResolveBankReceipt(ShopActor actor, string transactionId, string? reference, string? resolution)
    {
        actor.RequireAdmin();
        return store.Write(async db =>
        {
            var receipt = await db.First(db.Query<TransportBankReceipt>().Where(r => r.TransactionId == transactionId))
                ?? throw new ShopRuleException("Bank transaction not found.");
            if (receipt.Issue.Length == 0 || receipt.ResolvedAt.HasValue) return 0;
            var now = time.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var corrected = reference.Trim().ToUpperInvariant();
                if (corrected.Length != 35 || !Guid.TryParseExact(corrected[3..], "N", out var id))
                    throw new ShopRuleException("Enter the parcel's buyer transfer or remittance reference.");
                var (_, parcel) = await FindParcel(db, id);
                await ApplyBankReceipt(db, receipt, parcel, corrected, now);
                if (receipt.Issue.Length != 0) throw new ShopRuleException(receipt.Issue);
                receipt.Resolution = $"Matched to {corrected}";
            }
            else
                receipt.Resolution = ShopRules.Text(resolution, 250, "Verified external resolution");
            receipt.ResolvedAt = now;
            await commerce.AuditSellerTransportAsync(db, actor.UserId, "Transport bank exception reconciled", transactionId, now, receipt.Resolution);
            return 0;
        });
    }

    private async Task ApplyBankReceipt(IShopSession db, TransportBankReceipt receipt, TransportParcel parcel,
        string reference, DateTimeOffset now)
    {
        var id = parcel.SellerOrderId;
        var (child, _) = await FindParcel(db, id);
        var isDoor = reference == DoorReference(id);
        var isRemittance = reference == RemittanceReference(id);
        receipt.SellerOrderId = id;
        if (child.PaymentMethod != PaymentMethod.TransportAtDelivery || (!isDoor && !isRemittance)) return;
        receipt.Issue = "The transfer does not match the amount or stage expected for this parcel.";
        if (parcel.FundsVerifiedAt is not null)
        {
            receipt.Issue = "Funds have already been verified for this parcel.";
            return;
        }
        if (isDoor && parcel.Stage == TransportStage.PickedUp && parcel.DoorPayment is null && receipt.AmountVnd == child.TotalVnd)
            parcel.DoorPayment = DoorPayment.BankTransfer;
        else if (!isRemittance || parcel.Stage != TransportStage.Delivered || parcel.DoorPayment != DoorPayment.Cash ||
            receipt.AmountVnd != child.TotalVnd - child.ShippingVnd)
            return;
        parcel.FundsVerifiedAt = now;
        await commerce.OnDoorPaymentVerifiedAsync(db, id, $"BANK-{receipt.TransactionId}", postRetainedFee: isRemittance, now);
        receipt.Issue = "";
    }

    public Task<int> RecordShipperPayout(ShopActor actor, Guid id, string reference)
    {
        actor.RequireAdmin();
        reference = ShopRules.Text(reference, 100, "Payout reference");
        return store.Write(async db =>
        {
            var (_, parcel) = await FindParcel(db, id);
            if (parcel.ShipperPayoutReference == reference) return 0;
            if (parcel.Stage != TransportStage.Delivered || parcel.ShipperFeeVnd == 0 || parcel.ShipperPaidAt is not null ||
                await db.Count(db.Query<TransportParcel>().Where(p => p.ShipperPayoutReference == reference)) != 0)
                throw new ShopRuleException("This shipper payout cannot be applied.");
            var now = time.GetUtcNow();
            parcel.ShipperPayoutReference = reference;
            parcel.ShipperPaidAt = now;
            await commerce.PostShipperPayoutLedgerAsync(db, actor.UserId, id, parcel.ShipperFeeVnd, reference, now);
            return 0;
        });
    }

    public Task<(bool OnDuty, TransportJob? Job, Guid? AssignmentId)> ShipperJob(ShopActor actor) =>
        store.Read(async db =>
        {
            var shipper = await RequireShipper(db, actor);
            var assignment = await db.First(db.Query<TransportAssignment>().Where(a => a.ShipperId == actor.UserId && a.EndedAt == null));
            var parcel = assignment is null ? null : await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == assignment.SellerOrderId));
            return (shipper.OnDuty, parcel is null ? null : await Job(db, parcel, revealDestination: parcel.PickedUpAt.HasValue), assignment?.Id);
        });

    public Task<List<TransportJob>> ShipperRemittances(ShopActor actor) =>
        store.Read(async db =>
        {
            await RequireShipper(db, actor);
            var parcels = await db.List(db.Query<TransportParcel>().Where(p => p.ShipperId == actor.UserId &&
                p.Stage == TransportStage.Delivered && p.DoorPayment == DoorPayment.Cash && p.FundsVerifiedAt == null)
                .OrderBy(p => p.ShipperPaidAt).Take(50));
            var jobs = new List<TransportJob>(parcels.Count);
            foreach (var parcel in parcels) jobs.Add(await Job(db, parcel, revealDestination: false));
            return jobs;
        });

    public Task<List<TransportJob>> Operations(ShopActor actor)
    {
        actor.RequireStaff();
        return store.Read(async db =>
        {
            var parcels = await db.List(db.Query<TransportParcel>().Where(p =>
                p.Stage == TransportStage.WaitingForShipper || p.Stage == TransportStage.Assigned ||
                p.Stage == TransportStage.Acknowledged || p.Stage == TransportStage.Returning ||
                (p.Stage == TransportStage.PickedUp && p.FailedDeliveryCodes >= 5) ||
                p.Stage == TransportStage.Delivered &&
                ((p.DoorPayment == DoorPayment.Cash && p.FundsVerifiedAt == null) || (p.ShipperFeeVnd > 0 && p.ShipperPaidAt == null)))
                .OrderBy(p => p.RequestedAt).Take(100));
            var jobs = new List<TransportJob>(parcels.Count);
            foreach (var parcel in parcels) jobs.Add(await Job(db, parcel, revealDestination: false));
            return jobs;
        });
    }

    public Task<List<TransportBankReceipt>> BankExceptions(ShopActor actor)
    {
        actor.RequireAdmin();
        return store.Read(db => db.List(db.Query<TransportBankReceipt>().Where(r => r.Issue != "" && r.ResolvedAt == null)
            .OrderByDescending(r => r.ReceivedAt).Take(100)));
    }

    public async Task Tick(CancellationToken token)
    {
        var now = time.GetUtcNow();
        var expired = await store.Read(db => db.List(db.Query<TransportParcel>().Where(p =>
            (p.Stage == TransportStage.Packed && p.PickupDueAt <= now) ||
            (p.Stage == TransportStage.Assigned && p.AcknowledgeBy <= now))
            .OrderBy(p => p.PackedAt).Select(p => p.SellerOrderId).Take(100)));
        foreach (var id in expired)
        {
            token.ThrowIfCancellationRequested();
            await store.Write(async db =>
            {
                var (_, parcel) = await FindParcel(db, id);
                if (parcel.Stage == TransportStage.Assigned && parcel.AcknowledgeBy <= now)
                    await Requeue(db, parcel, now, "Timed out");
                if (parcel.Stage == TransportStage.Packed && parcel.PickupDueAt <= now)
                {
                    parcel.Stage = TransportStage.Cancelled;
                    parcel.DeliveryCode = "";
                    parcel.NextDispatchAt = null;
                    parcel.AcknowledgeBy = null;
                    await EndAssignment(db, parcel, now, "Cancelled");
                    await commerce.OnPickupDeadlineExpiredAsync(db, id, now);
                }
                return 0;
            });
        }
        var waiting = await store.Read(db => db.List(db.Query<TransportParcel>().Where(p =>
            p.Stage == TransportStage.WaitingForShipper && p.NextDispatchAt <= now)
            .OrderBy(p => p.RequestedAt).ThenBy(p => p.SellerOrderId).Take(25)));
        foreach (var parcel in waiting) await Assign(parcel, token);
    }

    private async Task Assign(TransportParcel waiting, CancellationToken token)
    {
        var id = waiting.SellerOrderId;
        var pickup = new TransportPoint(waiting.PickupLatitude, waiting.PickupLongitude);
        var candidates = await store.Read(db => db.List(Eligible(db, id, time.GetUtcNow())));
        var distances = new List<(TransportShipper Shipper, long Meters)>();
        foreach (var shipper in candidates)
        {
            var point = new TransportPoint(shipper.Latitude!.Value, shipper.Longitude!.Value);
            if (point.DistanceTo(pickup) > 10_000) continue;
            var distance = await routing.Distance(point, pickup, token);
            if (distance is <= 10_000) distances.Add((shipper, distance.Value));
        }
        await store.Write(async db =>
        {
            var (_, parcel) = await FindParcel(db, id);
            var now = time.GetUtcNow();
            if (parcel.Stage != TransportStage.WaitingForShipper || parcel.NextDispatchAt > now) return 0;
            foreach (var candidate in distances.OrderBy(d => d.Meters).ThenBy(d => d.Shipper.UserId, StringComparer.Ordinal))
            {
                var stillAvailable = await db.First(Eligible(db, id, now).Where(s =>
                    s.UserId == candidate.Shipper.UserId));
                if (stillAvailable is null) continue;
                parcel.Stage = TransportStage.Assigned;
                parcel.ShipperId = stillAvailable.UserId;
                parcel.AcknowledgeBy = now.AddMinutes(10);
                parcel.NextDispatchAt = null;
                db.Add(new TransportAssignment { SellerOrderId = id, ShipperId = stillAvailable.UserId, AssignedAt = now });
                return 0;
            }
            parcel.NextDispatchAt = now.AddMinutes(1);
            return 0;
        });
    }

    private static IQueryable<TransportShipper> Eligible(IShopSession db, Guid id, DateTimeOffset now)
    {
        var locationCutoff = now.AddMinutes(-2);
        var retryCutoff = now.AddMinutes(-30);
        return db.Query<TransportShipper>().Where(s => s.Active && s.OnDuty && s.LocatedAt >= locationCutoff &&
            s.Latitude != null && s.Longitude != null &&
            !db.Query<TransportAssignment>().Any(a => a.ShipperId == s.UserId &&
                (a.EndedAt == null || a.SellerOrderId == id && a.EndedAt >= retryCutoff)));
    }

    private static async Task Requeue(IShopSession db, TransportParcel parcel, DateTimeOffset now, string outcome)
    {
        await EndAssignment(db, parcel, now, outcome);
        parcel.Stage = TransportStage.WaitingForShipper;
        parcel.NextDispatchAt = now;
        parcel.ShipperId = null;
        parcel.AcknowledgeBy = null;
        parcel.AcknowledgedAt = null;
    }

    internal static async Task EndAssignment(IShopSession db, TransportParcel parcel, DateTimeOffset now, string outcome)
    {
        var assignment = await db.First(db.Query<TransportAssignment>().Where(a => a.SellerOrderId == parcel.SellerOrderId && a.EndedAt == null));
        if (assignment is null) return;
        assignment.EndedAt = now;
        assignment.Outcome = outcome;
    }

    private async Task<TransportJob> Job(IShopSession db, TransportParcel parcel, bool revealDestination)
    {
        var child = await commerce.GetOrderSnapshotAsync(db, parcel.SellerOrderId);
        parcel.DeliveryCode = "";
        var shipper = parcel.ShipperId is null ? null :
            await db.First(db.Query<TransportShipper>().Where(s => s.UserId == parcel.ShipperId));
        return new(parcel, child.OrderId, child.SellerOrderNumber, revealDestination ? child.Recipient : "", revealDestination ? child.Phone : "",
            revealDestination ? child.ShippingAddress : "", child.PaymentMethod, child.TotalVnd, child.ShippingVnd, shipper?.Email);
    }

    private Task<Guid> SellerAction(ShopActor actor, long sellerId, Guid id,
        Func<IShopSession, TransportOrderSnapshot, TransportParcel, DateTimeOffset, Task> action) =>
        store.Write(async db =>
        {
            var child = await commerce.RequireSellerOrderForTransportAsync(db, actor, sellerId, id);
            var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == id))
                ?? throw new ShopRuleException("Transport parcel not found.");
            await action(db, child, parcel, time.GetUtcNow());
            return child.OrderId;
        });

    private async Task<(TransportOrderSnapshot Child, TransportParcel Parcel)> AssignedParcel(IShopSession db, ShopActor actor, Guid id)
    {
        await RequireShipper(db, actor);
        var result = await FindParcel(db, id);
        if (result.Parcel.ShipperId != actor.UserId) throw new ShopRuleException("The parcel is assigned to another shipper.");
        return result;
    }

    private static async Task<TransportShipper> RequireShipper(IShopSession db, ShopActor actor)
    {
        if (!actor.Mfa) throw new ShopRuleException("Shippers must sign in with MFA.");
        return await db.First(db.Query<TransportShipper>().Where(s => s.UserId == actor.UserId && s.Active))
            ?? throw new ShopRuleException("This account is not an active HKShop shipper.");
    }

    private async Task<(TransportOrderSnapshot Child, TransportParcel Parcel)> FindParcel(IShopSession db, Guid id)
    {
        var child = await commerce.GetOrderSnapshotAsync(db, id);
        var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == id))
            ?? throw new ShopRuleException("Transport parcel not found.");
        return (child, parcel);
    }
}
