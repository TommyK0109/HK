using HKShop.Contracts.Ports;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;

namespace HKShop.Contracts.Events;

public static class KafkaTopics
{
    public const string Identity = "hkshop.identity.events.v1";
    public const string Catalog = "hkshop.catalog.events.v1";
    public const string Ordering = "hkshop.ordering.events.v1";
    public const string Payments = "hkshop.payments.events.v1";
    public const string Shipping = "hkshop.shipping.events.v1";

    public static string Dlq(string topic, string consumer) => $"{topic}.{consumer}.dlq.v1";
}

// Identity events (hkshop.identity.events.v1)
public sealed record UserRegistered(
    string UserId,
    string Email,
    string DisplayName,
    string ConfirmationUrl,
    DateTimeOffset OccurredAt);

public sealed record EmailConfirmationRequested(
    string UserId,
    string Email,
    string ConfirmationUrl,
    DateTimeOffset OccurredAt);

public sealed record PasswordResetRequested(
    string UserId,
    string Email,
    string ResetUrl,
    DateTimeOffset OccurredAt);

public sealed record UserDisabled(
    string UserId,
    string Email,
    DateTimeOffset OccurredAt);

public sealed record UserRolesChanged(
    string UserId,
    string Email,
    IReadOnlyList<string> Roles,
    bool ActiveShipper,
    DateTimeOffset OccurredAt);

// Catalog events (hkshop.catalog.events.v1)
public sealed record StockReservationLine(
    long OfferId,
    long SkuId,
    long SellerId,
    int Quantity,
    decimal ExpectedUnitPriceVnd,
    Guid? SellerOrderId = null);

public sealed record StockReserved(
    Guid OrderId,
    IReadOnlyList<StockReservationLine> Lines,
    DateTimeOffset ReservedAt);

public sealed record StockReservationFailed(
    Guid OrderId,
    string Reason,
    DateTimeOffset FailedAt);

public sealed record StockReleased(
    Guid OrderId,
    Guid? SellerOrderId,
    string Reason,
    DateTimeOffset ReleasedAt);

public sealed record OfferChanged(
    long OfferId,
    long SkuId,
    long SellerId,
    string SellerName,
    string SellerSkuCode,
    string Slug,
    string Name,
    decimal PriceVnd,
    int Available,
    bool Active,
    DateTimeOffset OccurredAt);

public sealed record SellerStatusChanged(
    long SellerId,
    string DisplayName,
    SellerStatus Status,
    DateTimeOffset OccurredAt);

public sealed record SellerMembershipChanged(
    long SellerId,
    string SellerName,
    SellerStatus SellerStatus,
    string UserId,
    string Email,
    SellerRole Role,
    SellerMembershipStatus Status,
    DateTimeOffset OccurredAt);

public sealed record SellerLocationChanged(
    long SellerId,
    long LocationId,
    string Name,
    string PickupAddress,
    double? Latitude,
    double? Longitude,
    bool Active,
    DateTimeOffset OccurredAt);

public sealed record SellerInvited(
    long InvitationId,
    long SellerId,
    string StoreName,
    string Email,
    string AcceptUrl,
    DateTimeOffset OccurredAt);

// Ordering events (hkshop.ordering.events.v1)
public sealed record OrderPlacementRequested(
    Guid OrderId,
    string OrderNumber,
    string CustomerId,
    IReadOnlyList<StockReservationLine> Lines,
    DateTimeOffset RequestedAt);

public sealed record PaymentSellerSplit(
    Guid SellerOrderId,
    long SellerId,
    string SellerOrderNumber,
    decimal SubtotalVnd,
    decimal SellerDiscountVnd,
    decimal PlatformDiscountVnd,
    decimal ShippingVnd,
    decimal TotalVnd,
    decimal CommissionVnd);

public sealed record PaymentRequested(
    Guid OrderId,
    string OrderNumber,
    string CustomerId,
    string CustomerEmail,
    PaymentMethod Method,
    decimal AmountVnd,
    DateTimeOffset Deadline,
    IReadOnlyList<PaymentSellerSplit> SellerOrders,
    DateTimeOffset RequestedAt);

public sealed record OrderCancelled(
    Guid OrderId,
    string OrderNumber,
    string CustomerEmail,
    string Reason,
    IReadOnlyList<Guid> SellerOrderIds,
    DateTimeOffset CancelledAt);

public sealed record SellerOrderPlaced(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    OrderStatus Status,
    decimal TotalVnd,
    decimal ShippingVnd,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string Recipient,
    string Phone,
    string ShippingAddress,
    NewTransportParcel? Parcel,
    DateTimeOffset PlacedAt);

public sealed record SellerOrderPacked(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    DateTimeOffset PackedAt);

public sealed record SellerOrderDispatched(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    string CustomerEmail,
    string Carrier,
    string TrackingNumber,
    IReadOnlyList<StockReservationLine> Lines,
    DateTimeOffset DispatchedAt);

public sealed record SellerOrderDelivered(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    string CustomerId,
    string CustomerEmail,
    IReadOnlyList<int> ProductIds,
    DateTimeOffset DeliveredAt);

public sealed record ReturnInspected(
    Guid ReturnId,
    Guid OrderId,
    Guid SellerOrderId,
    long SellerId,
    IReadOnlyList<StockReservationLine> RestockedLines,
    DateTimeOffset InspectedAt);

public sealed record RefundLineAllocationDto(
    long OrderItemId,
    int Quantity,
    decimal AmountVnd,
    decimal CommissionReversalVnd,
    decimal PlatformDiscountReversalVnd);

public sealed record RefundRequested(
    Guid OrderId,
    Guid SellerOrderId,
    long SellerId,
    Guid? ReturnId,
    string BusinessKey,
    decimal AmountVnd,
    decimal ShippingVnd,
    IReadOnlyList<RefundLineAllocationDto> Lines,
    DateTimeOffset RequestedAt);

// Payments events (hkshop.payments.events.v1)
public sealed record PaymentCaptured(
    Guid PaymentAttemptId,
    Guid OrderId,
    string OrderNumber,
    decimal CapturedAmountVnd,
    string? ProviderTransaction,
    DateTimeOffset PaidAt);

public sealed record PaymentFailed(
    Guid PaymentAttemptId,
    Guid OrderId,
    string OrderNumber,
    string CustomerEmail,
    string Reason,
    DateTimeOffset FailedAt);

public sealed record PaymentExpired(
    Guid OrderId,
    string OrderNumber,
    string CustomerEmail,
    DateTimeOffset ExpiredAt);

public sealed record RefundCompletedEvent(
    long RefundId,
    Guid OrderId,
    string OrderNumber,
    string CustomerEmail,
    Guid? SellerOrderId,
    Guid? PaymentAttemptId,
    decimal AmountVnd,
    decimal ShippingVnd,
    bool Simulated,
    string Reference,
    DateTimeOffset CompletedAt);

public sealed record PayoutApproved(
    Guid PayoutId,
    long SellerId,
    decimal AmountVnd,
    DateTimeOffset ApprovedAt);

public sealed record PayoutPaid(
    Guid PayoutId,
    long SellerId,
    decimal AmountVnd,
    string ProviderReference,
    DateTimeOffset PaidAt);

// Shipping events (hkshop.shipping.events.v1)
public sealed record ParcelPickedUp(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    string ShipperId,
    DateTimeOffset PickedUpAt);

public sealed record ParcelDelivered(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string ShipperId,
    DoorPayment? DoorPayment,
    bool FundsVerified,
    decimal ShipperFeeVnd,
    DateTimeOffset DeliveredAt);

public sealed record DoorPaymentVerified(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    decimal AmountVnd,
    decimal ShippingVnd,
    string? TransactionReference,
    bool PostRetainedFee,
    DateTimeOffset VerifiedAt);

public sealed record PickupDeadlineExpired(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    DateTimeOffset ExpiredAt);

public sealed record ParcelReturnedToSender(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string Issue,
    DateTimeOffset OccurredAt);

public sealed record ShipperFeeDue(
    Guid SellerOrderId,
    Guid OrderId,
    decimal ShipperFeeVnd,
    string Reference,
    string? ActorId,
    DateTimeOffset PaidAt);
