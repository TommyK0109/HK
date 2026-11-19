using HKShop.Application;
using HKShop.Domain.Orders;

namespace HKShop.Contracts.Ports;

public sealed record TransportOperationRow(
    TransportParcelSummary Parcel,
    Guid OrderId,
    string Number,
    string Recipient,
    string Phone,
    string Destination,
    PaymentMethod Method,
    decimal TotalVnd,
    decimal ShippingVnd,
    string? ShipperEmail);

public sealed record TransportBankExceptionRow(
    string TransactionId,
    string Reference,
    decimal AmountVnd,
    DateTimeOffset ReceivedAt,
    Guid? SellerOrderId,
    string Issue,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

public sealed record TransportJobDto(
    TransportParcelSummary Parcel,
    Guid OrderId,
    string Number,
    string Recipient,
    string Phone,
    string Destination,
    PaymentMethod Method,
    decimal TotalVnd,
    decimal ShippingVnd,
    string? ShipperEmail);

public sealed record ShipperStateDto(
    bool OnDuty,
    TransportJobDto? ActiveJob,
    Guid? AssignmentId,
    IReadOnlyList<TransportJobDto> Remittances);

public interface IShippingShipperPort
{
    Task<ShipperStateDto> ShipperStateAsync(ShopActor actor, CancellationToken cancellationToken = default);
    Task UpdatePresenceAsync(ShopActor actor, bool onDuty, double? latitude, double? longitude, CancellationToken cancellationToken = default);
    Task RespondAsync(ShopActor actor, Guid sellerOrderId, Guid assignmentId, bool accept, CancellationToken cancellationToken = default);
    Task<bool> DeliverAsync(ShopActor actor, Guid sellerOrderId, string code, DoorPayment? method, decimal? cash, CancellationToken cancellationToken = default);
    Task ReturnToSenderAsync(ShopActor actor, Guid sellerOrderId, string issue, CancellationToken cancellationToken = default);
}

public interface IShippingAdminPort
{
    Task ConfigureShipperAsync(IShopSession db, ShopActor actor, string userId, string email, bool active);
    Task ReassignAsync(ShopActor actor, Guid sellerOrderId);
    Task ResetDeliveryConfirmationAsync(ShopActor actor, Guid sellerOrderId);
    Task RecordShipperPayoutAsync(ShopActor actor, Guid sellerOrderId, string reference);
    Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(ShopActor actor);
    Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(ShopActor actor);
    Task ResolveBankReceiptAsync(ShopActor actor, string transactionId, string? reference, string? resolution);
}
