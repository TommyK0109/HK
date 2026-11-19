using HKShop.Application;
using HKShop.Domain.Orders;

namespace HKShop.Contracts.Ports;

public sealed record TransportOrderSnapshot(
    Guid SellerOrderId,
    Guid OrderId,
    long SellerId,
    string SellerOrderNumber,
    OrderStatus SellerOrderStatus,
    decimal TotalVnd,
    decimal ShippingVnd,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string Recipient,
    string Phone,
    string ShippingAddress);

public interface IShippingCommerceCallbackPort
{
    Task<SellerLocationSummary?> GetSellerLocationAsync(IShopSession db, ShopActor actor, long sellerId);
    Task SetSellerLocationAsync(IShopSession db, ShopActor actor, long sellerId, string address, double latitude, double longitude, DateTimeOffset now);
    Task<TransportOrderSnapshot> RequireSellerOrderForTransportAsync(IShopSession db, ShopActor actor, long sellerId, Guid sellerOrderId);
    Task<TransportOrderSnapshot> GetOrderSnapshotAsync(IShopSession db, Guid sellerOrderId);
    Task AuditSellerTransportAsync(IShopSession db, string? actorId, string action, string target, DateTimeOffset now, string reason = "", long? sellerId = null);
    Task NotifyReturnToSenderAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now);
    Task OnParcelPickedUpAsync(IShopSession db, ShopActor actor, Guid sellerOrderId, DateTimeOffset now);
    Task OnParcelDeliveredAsync(IShopSession db, ShopActor actor, Guid sellerOrderId, DoorPayment? doorPayment, bool fundsVerified, decimal shipperFeeVnd, DateTimeOffset now);
    Task OnDoorPaymentVerifiedAsync(IShopSession db, Guid sellerOrderId, string? transactionReference, bool postRetainedFee, DateTimeOffset now);
    Task OnPickupDeadlineExpiredAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now);
    Task PostShipperPayoutLedgerAsync(IShopSession db, string? actorId, Guid sellerOrderId, decimal shipperFeeVnd, string reference, DateTimeOffset now);
    Task RequireShipperEligibilityAsync(IShopSession db, string userId);
}
