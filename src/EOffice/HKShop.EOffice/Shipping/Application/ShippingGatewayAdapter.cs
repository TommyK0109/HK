using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;

namespace HKShop.EOffice.Shipping.Application;

public sealed class ShippingGatewayAdapter(
    TransportService transport,
    TransportSettings settings,
    ITransportRouting routing) : IShippingGateway, IShippingAdminPort, IShippingShipperPort
{
    public bool Enabled => settings.Enabled;

    public async Task<IReadOnlyList<TransportRoute>> QuoteRoutesAsync(
        TransportPoint destination,
        IReadOnlyList<SellerRouteOrigin> origins,
        IReadOnlyList<TransportRoute>? acceptedRoutes,
        bool quoteNewRoutes,
        CancellationToken cancellationToken = default)
    {
        var routes = new List<TransportRoute>(origins.Count);
        foreach (var sellerOrigin in origins)
        {
            var origin = TransportPoint.Require(sellerOrigin.Latitude, sellerOrigin.Longitude, "the storage location");
            long meters;
            if (quoteNewRoutes)
                meters = await routing.Distance(origin, destination, cancellationToken)
                    ?? throw new ShopRuleException("No road route was found from storage to your address. Check your delivery pin.");
            else
            {
                var saved = acceptedRoutes?.SingleOrDefault(r => r.SellerId == sellerOrigin.SellerId);
                if (saved is null || saved.LocationId != sellerOrigin.LocationId || saved.PickupAddress != sellerOrigin.PickupAddress ||
                    saved.Origin != origin || saved.Destination != destination)
                    throw new ShopRuleException("The delivery route or pickup location changed. Please review your order again.");
                meters = saved.DistanceMeters;
            }
            routes.Add(new(sellerOrigin.SellerId, sellerOrigin.LocationId, sellerOrigin.PickupAddress, origin, destination, meters));
        }
        return routes;
    }

    public Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> RegisterParcelsAsync(
        IShopSession db,
        IReadOnlyList<NewTransportParcel> parcels,
        CancellationToken cancellationToken = default)
    {
        var summaries = new Dictionary<Guid, TransportParcelSummary>(parcels.Count);
        foreach (var item in parcels)
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
            db.Add(parcel);
            summaries[item.SellerOrderId] = parcel.ToSummary();
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, TransportParcelSummary>>(summaries);
    }

    public Task<SellerLocationSummary?> GetSellerLocationAsync(ShopActor actor, long sellerId) =>
        transport.Location(actor, sellerId);

    public async Task SetSellerLocationAsync(ShopActor actor, long sellerId, string address, double latitude, double longitude) =>
        await transport.SetLocation(actor, sellerId, address, latitude, longitude);

    public Task<Guid> MarkParcelPackedAsync(ShopActor actor, long sellerId, Guid sellerOrderId) =>
        transport.MarkPacked(actor, sellerId, sellerOrderId);

    public Task<Guid> RequestParcelPickupAsync(ShopActor actor, long sellerId, Guid sellerOrderId) =>
        transport.RequestPickup(actor, sellerId, sellerOrderId);

    public Task<Guid> ConfirmParcelHandoffAsync(ShopActor actor, long sellerId, Guid sellerOrderId, string code) =>
        transport.ConfirmPickup(actor, sellerId, sellerOrderId, code);

    public async Task CancelOrderParcelsAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now)
    {
        var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == sellerOrderId));
        if (parcel is null) return;
        parcel.Stage = TransportStage.Cancelled;
        parcel.DeliveryCode = "";
        parcel.NextDispatchAt = null;
        parcel.AcknowledgeBy = null;
        await TransportService.EndAssignment(db, parcel, now, "Cancelled");
    }

    public async Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> GetParcelSummariesAsync(
        IShopSession db,
        IReadOnlyCollection<Guid> sellerOrderIds,
        bool redactDeliveryCode = false)
    {
        if (sellerOrderIds.Count == 0)
            return new Dictionary<Guid, TransportParcelSummary>();
        var rows = await db.List(db.Query<TransportParcel>().Where(p => sellerOrderIds.Contains(p.SellerOrderId)));
        return rows.ToDictionary(p => p.SellerOrderId, p => p.ToSummary(redactDeliveryCode));
    }

    public async Task<bool> HasUnverifiedCashRemittanceAsync(IShopSession db, Guid sellerOrderId)
    {
        var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == sellerOrderId));
        return parcel is not null && parcel.DoorPayment == DoorPayment.Cash && parcel.FundsVerifiedAt is null;
    }

    public async Task CompleteReturnedParcelAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now)
    {
        var parcel = await db.First(db.Query<TransportParcel>().Where(p => p.SellerOrderId == sellerOrderId));
        if (parcel is null) return;
        parcel.Stage = TransportStage.Returned;
        parcel.DeliveryCode = "";
        await TransportService.EndAssignment(db, parcel, now, "Returned");
    }

    public Task ConfigureShipperAsync(IShopSession db, ShopActor actor, string userId, string email, bool active) =>
        transport.ConfigureShipper(db, actor, userId, email, active);

    public async Task ReassignAsync(ShopActor actor, Guid sellerOrderId) =>
        await transport.Reassign(actor, sellerOrderId);

    public async Task ResetDeliveryConfirmationAsync(ShopActor actor, Guid sellerOrderId) =>
        await transport.ResetDeliveryConfirmation(actor, sellerOrderId);

    public async Task RecordShipperPayoutAsync(ShopActor actor, Guid sellerOrderId, string reference) =>
        await transport.RecordShipperPayout(actor, sellerOrderId, reference);

    public async Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(ShopActor actor)
    {
        var jobs = await transport.Operations(actor);
        return jobs.Select(j => new TransportOperationRow(
            j.Parcel.ToSummary(redactDeliveryCode: true),
            j.OrderId,
            j.Number,
            j.Recipient,
            j.Phone,
            j.Destination,
            j.Method,
            j.TotalVnd,
            j.ShippingVnd,
            j.ShipperEmail)).ToList();
    }

    public async Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(ShopActor actor)
    {
        var rows = await transport.BankExceptions(actor);
        return rows.Select(r => new TransportBankExceptionRow(
            r.TransactionId,
            r.Reference,
            r.AmountVnd,
            r.ReceivedAt,
            r.SellerOrderId,
            r.Issue,
            r.ResolvedAt,
            r.Resolution)).ToList();
    }

    public async Task ResolveBankReceiptAsync(ShopActor actor, string transactionId, string? reference, string? resolution) =>
        await transport.ResolveBankReceipt(actor, transactionId, reference, resolution);

    public async Task<ShipperStateDto> ShipperStateAsync(ShopActor actor, CancellationToken cancellationToken = default)
    {
        var (onDuty, job, assignmentId) = await transport.ShipperJob(actor);
        var remittances = await transport.ShipperRemittances(actor);
        return new ShipperStateDto(
            onDuty,
            job is null ? null : MapJob(job),
            assignmentId,
            remittances.Select(MapJob).ToList());
    }

    public async Task UpdatePresenceAsync(ShopActor actor, bool onDuty, double? latitude, double? longitude, CancellationToken cancellationToken = default) =>
        await transport.UpdatePresence(actor, onDuty, latitude, longitude);

    public async Task RespondAsync(ShopActor actor, Guid sellerOrderId, Guid assignmentId, bool accept, CancellationToken cancellationToken = default) =>
        await transport.Respond(actor, sellerOrderId, assignmentId, accept);

    public Task<bool> DeliverAsync(ShopActor actor, Guid sellerOrderId, string code, DoorPayment? method, decimal? cash, CancellationToken cancellationToken = default) =>
        transport.Deliver(actor, sellerOrderId, code, method, cash);

    public async Task ReturnToSenderAsync(ShopActor actor, Guid sellerOrderId, string issue, CancellationToken cancellationToken = default) =>
        await transport.ReturnToSender(actor, sellerOrderId, issue);

    private static TransportJobDto MapJob(TransportJob j) => new(
        j.Parcel.ToSummary(redactDeliveryCode: true),
        j.OrderId,
        j.Number,
        j.Recipient,
        j.Phone,
        j.Destination,
        j.Method,
        j.TotalVnd,
        j.ShippingVnd,
        j.ShipperEmail);
}
