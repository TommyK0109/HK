using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Domain.Orders;

namespace HKShop.Contracts.Ports;

public sealed class TransportParcelSummary
{
    public Guid SellerOrderId { get; set; }
    public TransportStage Stage { get; set; }
    public long LocationId { get; set; }
    public string PickupAddress { get; set; } = "";
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public long DistanceMeters { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DeliveryCode { get; set; } = "";
    public DateTimeOffset? PackedAt { get; set; }
    public DateTimeOffset? PickupDueAt { get; set; }
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? NextDispatchAt { get; set; }
    public string? ShipperId { get; set; }
    public DateTimeOffset? AcknowledgeBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? PickedUpAt { get; set; }
    public DoorPayment? DoorPayment { get; set; }
    public DateTimeOffset? FundsVerifiedAt { get; set; }
    public decimal CashCollectedVnd { get; set; }
    public decimal ShipperFeeVnd { get; set; }
    public DateTimeOffset? ShipperPaidAt { get; set; }
    public string? ShipperPayoutReference { get; set; }
    public string Issue { get; set; } = "";
    public int FailedDeliveryCodes { get; set; }
}

public sealed record SellerLocationSummary(
    long Id,
    long SellerId,
    string Name,
    string? PickupAddress,
    double? Latitude,
    double? Longitude,
    bool Active);

public sealed record NewTransportParcel(
    Guid SellerOrderId,
    long LocationId,
    string PickupAddress,
    TransportPoint Origin,
    TransportPoint Destination,
    long DistanceMeters,
    string DeliveryCode);

public sealed record SellerRouteOrigin(
    long SellerId,
    long LocationId,
    string PickupAddress,
    double? Latitude,
    double? Longitude);

public interface IShippingGateway
{
    bool Enabled { get; }
    Task<IReadOnlyList<TransportRoute>> QuoteRoutesAsync(
        TransportPoint destination,
        IReadOnlyList<SellerRouteOrigin> origins,
        IReadOnlyList<TransportRoute>? acceptedRoutes,
        bool quoteNewRoutes,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> RegisterParcelsAsync(
        IShopSession db,
        IReadOnlyList<NewTransportParcel> parcels,
        CancellationToken cancellationToken = default);
    Task<SellerLocationSummary?> GetSellerLocationAsync(ShopActor actor, long sellerId);
    Task SetSellerLocationAsync(ShopActor actor, long sellerId, string address, double latitude, double longitude);
    Task<Guid> MarkParcelPackedAsync(ShopActor actor, long sellerId, Guid sellerOrderId);
    Task<Guid> RequestParcelPickupAsync(ShopActor actor, long sellerId, Guid sellerOrderId);
    Task<Guid> ConfirmParcelHandoffAsync(ShopActor actor, long sellerId, Guid sellerOrderId, string code);
    Task CancelOrderParcelsAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now);
    Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> GetParcelSummariesAsync(
        IShopSession db,
        IReadOnlyCollection<Guid> sellerOrderIds,
        bool redactDeliveryCode = false);
    Task<bool> HasUnverifiedCashRemittanceAsync(IShopSession db, Guid sellerOrderId);
    Task CompleteReturnedParcelAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now);
}
