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

public interface IShippingAdminPort
{
    Task ConfigureShipperAsync(ShopActor actor, string userId, string email, bool active);
    Task ReassignAsync(ShopActor actor, Guid sellerOrderId);
    Task ResetDeliveryConfirmationAsync(ShopActor actor, Guid sellerOrderId);
    Task RecordShipperPayoutAsync(ShopActor actor, Guid sellerOrderId, string reference);
    Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(ShopActor actor);
    Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(ShopActor actor);
    Task ResolveBankReceiptAsync(ShopActor actor, string transactionId, string? reference, string? resolution);
}
