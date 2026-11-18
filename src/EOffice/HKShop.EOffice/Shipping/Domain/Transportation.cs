using HKShop.Contracts.Ports;

namespace HKShop.Domain.Orders;

public sealed class TransportParcel
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

    public TransportParcelSummary ToSummary(bool redactDeliveryCode = false) => new()
    {
        SellerOrderId = SellerOrderId,
        Stage = Stage,
        LocationId = LocationId,
        PickupAddress = PickupAddress,
        PickupLatitude = PickupLatitude,
        PickupLongitude = PickupLongitude,
        DestinationLatitude = DestinationLatitude,
        DestinationLongitude = DestinationLongitude,
        DistanceMeters = DistanceMeters,
        DeliveryCode = redactDeliveryCode ? "" : DeliveryCode,
        PackedAt = PackedAt,
        PickupDueAt = PickupDueAt,
        RequestedAt = RequestedAt,
        NextDispatchAt = NextDispatchAt,
        ShipperId = ShipperId,
        AcknowledgeBy = AcknowledgeBy,
        AcknowledgedAt = AcknowledgedAt,
        PickedUpAt = PickedUpAt,
        DoorPayment = DoorPayment,
        FundsVerifiedAt = FundsVerifiedAt,
        CashCollectedVnd = CashCollectedVnd,
        ShipperFeeVnd = ShipperFeeVnd,
        ShipperPaidAt = ShipperPaidAt,
        ShipperPayoutReference = ShipperPayoutReference,
        Issue = Issue,
        FailedDeliveryCodes = FailedDeliveryCodes
    };
}

public sealed class TransportShipper
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public bool Active { get; set; }
    public bool OnDuty { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTimeOffset? LocatedAt { get; set; }
}

public sealed class TransportAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SellerOrderId { get; set; }
    public string ShipperId { get; set; } = "";
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class TransportBankReceipt
{
    public string TransactionId { get; set; } = "";
    public string Reference { get; set; } = "";
    public decimal AmountVnd { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? SellerOrderId { get; set; }
    public string Issue { get; set; } = "";
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}
