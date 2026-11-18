using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Application.Orders
{
    public sealed record ReturnQuantity(long ItemId, int Quantity);
    public sealed record InspectedItem(long ItemId, int Accepted, int Restocked);
}

namespace HKShop.Application.Payments
{
    public sealed record PaymentIssue(
        Guid OrderId,
        string Number,
        string ProviderInvoice,
        PaymentStatus Status,
        decimal ExpectedVnd,
        decimal CapturedVnd,
        string Issue,
        DateTimeOffset CreatedAt);
}

namespace HKShop.Application.Settlement
{
    public sealed record SettlementPaymentRow(Guid OrderId, Guid PaymentAttemptId, string OrderNumber, string? ProviderReference,
        decimal CapturedVnd, decimal PendingRefundVnd, decimal CompletedRefundVnd, DateTimeOffset? PaidAt, bool Applied, bool Posted, string Issue);
    public sealed record SettlementRefundRow(long RefundId, Guid OrderId, string OrderNumber, Guid? PaymentAttemptId, string Reference,
        decimal AmountVnd, RefundStatus Status, DateTimeOffset? CompletedAt, bool Posted);
}

namespace HKShop.Application.Sellers
{
    public sealed record PlatformSeller(long Id, string Slug, string DisplayName, SellerStatus Status, DateTimeOffset CreatedAt, string? OwnerEmail);
    public sealed record OfferReviewRow(long Id, string Seller, string Product, string Variant, string Code, decimal? PriceVnd);
    public sealed record ProductProposalReview(long Id, string Seller, string Name, string Slug, string Description, string Category);
    public sealed record MarketplaceModeration(Page<OfferReviewRow> Offers, Page<ProductProposalReview> Proposals);
}

namespace HKShop.Application.Reporting
{
    public sealed record SalesSummary(decimal Collected, decimal Refunded, IReadOnlyList<StatusCount> Orders)
    {
        public decimal Net => Collected - Refunded;
    }
    public sealed record StatusCount(OrderStatus Status, int Count);
}

namespace HKShop.Contracts.Ports
{
    using HKShop.Application;
    using HKShop.Application.Orders;
    using HKShop.Application.Payments;
    using HKShop.Application.Reporting;
    using HKShop.Application.Sellers;
    using HKShop.Application.Settlement;

    public sealed record PromotionDto(
        long Id,
        string Code,
        bool Active,
        bool IsPercentage,
        decimal Value,
        decimal MinimumVnd,
        int UsageCap,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt);

    public sealed record EOfficeOrderSummaryDto(
        Guid Id,
        string Number,
        DateTimeOffset CreatedAt,
        OrderStatus Status,
        decimal TotalVnd);

    public sealed record EOfficeOrderItemDto(
        long Id,
        Guid OrderId,
        Guid SellerOrderId,
        long SellerId,
        long SkuId,
        long OfferId,
        string SkuCode,
        string SellerSkuCode,
        string Name,
        int Quantity,
        decimal UnitPriceVnd,
        decimal DiscountVnd);

    public sealed record EOfficeShipmentDto(
        Guid SellerOrderId,
        Guid OrderId,
        string Carrier,
        string TrackingNumber,
        DateTimeOffset DispatchedAt);

    public sealed record EOfficeSellerOrderDto(
        Guid Id,
        Guid OrderId,
        long SellerId,
        string Number,
        OrderStatus Status,
        decimal SubtotalVnd,
        decimal SellerDiscountVnd,
        decimal PlatformDiscountVnd,
        decimal ShippingVnd,
        decimal TotalVnd,
        decimal CommissionVnd,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DeliveredAt,
        EOfficeShipmentDto? Shipment,
        TransportParcelSummary? Transport);

    public sealed record EOfficePaymentAttemptDto(
        Guid Id,
        Guid OrderId,
        Guid? SellerOrderId,
        int AttemptNumber,
        PaymentMethod Method,
        PaymentStatus Status,
        decimal AmountVnd,
        decimal CapturedAmountVnd,
        string? ProviderInvoice,
        string? ProviderTransaction,
        bool AppliedToOrder,
        string Issue);

    public sealed record EOfficePaymentDto(
        Guid OrderId,
        PaymentMethod Method,
        PaymentStatus Status,
        decimal AmountVnd,
        string Issue);

    public sealed record EOfficeOrderEventDto(
        long Id,
        OrderStatus Status,
        DateTimeOffset OccurredAt,
        string Note);

    public sealed record EOfficeReturnItemDto(
        long Id,
        Guid ReturnId,
        Guid SellerOrderId,
        long OrderItemId,
        int Requested,
        int Accepted,
        int Restocked);

    public sealed record EOfficeReturnDto(
        Guid Id,
        Guid OrderId,
        Guid SellerOrderId,
        ReturnStatus Status,
        string Reason,
        string StaffNote,
        DateTimeOffset RequestedAt,
        IReadOnlyList<EOfficeReturnItemDto> Items);

    public sealed record EOfficeRefundDto(
        long Id,
        Guid OrderId,
        Guid? PaymentAttemptId,
        Guid? SellerOrderId,
        Guid? ReturnId,
        string BusinessKey,
        decimal AmountVnd,
        decimal ShippingVnd,
        RefundStatus Status,
        bool Simulated,
        string Reference,
        string ProviderReference,
        string Issue,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset RequestedAt,
        DateTimeOffset? CompletedAt);

    public sealed record EOfficeOrderDetailsDto(
        Guid Id,
        long? SellerId,
        string Number,
        DateTimeOffset CreatedAt,
        OrderStatus Status,
        PaymentStatus PaymentStatus,
        string Recipient,
        string Phone,
        string ShippingAddress,
        decimal ShippingVnd,
        decimal TotalVnd,
        EOfficePaymentDto Payment,
        EOfficeShipmentDto? Shipment,
        IReadOnlyList<EOfficeSellerOrderDto> SellerOrders,
        IReadOnlyList<EOfficeOrderItemDto> Items,
        IReadOnlyList<EOfficeOrderEventDto> Events,
        IReadOnlyList<EOfficePaymentAttemptDto> PaymentAttempts,
        IReadOnlyList<EOfficeReturnDto> Returns,
        IReadOnlyList<EOfficeRefundDto> Refunds,
        IReadOnlyDictionary<long, string> SellerNames);

    public sealed record PayoutQueueItemDto(
        Guid Id,
        long SellerId,
        string SellerName,
        decimal AmountVnd,
        PayoutStatus Status,
        string ProviderReference,
        string Issue,
        string ReviewNote,
        DateTimeOffset RequestedAt,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? CompletedAt);

    public sealed record AuditEntryDto(
        long Id,
        long? SellerId,
        string? ActorId,
        string Action,
        string Target,
        string Reason,
        DateTimeOffset OccurredAt);

    public sealed record FailedMailDto(
        long Id,
        string Subject,
        int Attempts);

    public interface ICommerceOperationsPort
    {
        Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(ShopActor actor);
        Task<int> SavePromotionAsync(ShopActor actor, long id, string code, bool active, bool percentage,
            decimal value, decimal minimum, int cap, DateTimeOffset start, DateTimeOffset end);
        Task<Page<EOfficeOrderSummaryDto>> ListOrdersAsync(ShopActor actor, OrderStatus? status, int page);
        Task<EOfficeOrderDetailsDto?> OrderDetailsAsync(ShopActor actor, Guid id);
        Task<int> CancelOrderAsync(ShopActor actor, Guid id, string reason);
        Task<Guid> CancelSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string reason);
        Task<int> StartPackingOrderAsync(ShopActor actor, Guid id);
        Task<Guid> StartPackingSellerOrderAsync(ShopActor actor, Guid sellerOrderId);
        Task<int> DispatchOrderAsync(ShopActor actor, Guid id, string carrier, string tracking);
        Task<Guid> DispatchSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string carrier, string tracking);
        Task<int> DeliverOrderAsync(ShopActor actor, Guid id, decimal? collected, string reference);
        Task<Guid> DeliverSellerOrderAsync(ShopActor actor, Guid sellerOrderId, decimal? collected, string reference);
        Task<int> DeliveryExceptionOrderAsync(ShopActor actor, Guid id, string note);
        Task<Guid> DeliveryExceptionSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string note);
        Task<int> ReceiveUndeliveredOrderAsync(ShopActor actor, Guid id, string reason, IReadOnlyList<ReturnQuantity> quantities);
        Task<Guid> ReceiveUndeliveredSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string reason, IReadOnlyList<ReturnQuantity> quantities);
        Task<IReadOnlyList<EOfficeReturnDto>> ReturnQueueAsync(ShopActor actor);
        Task<Guid> ReviewReturnAsync(ShopActor actor, Guid id, bool approved, string note);
        Task<Guid> InspectReturnAsync(ShopActor actor, Guid id, IReadOnlyList<InspectedItem> inspection, string note);
        Task<IReadOnlyList<PaymentIssue>> PaymentIssuesAsync(ShopActor actor);
        Task<IReadOnlyList<EOfficeRefundDto>> PendingRefundsAsync(ShopActor actor);
        Task<IReadOnlyList<SettlementPaymentRow>> RecentLedgerPaymentsAsync(ShopActor actor);
        Task<IReadOnlyList<SettlementRefundRow>> RecentLedgerRefundsAsync(ShopActor actor);
        Task ReconcilePaymentAsync(ShopActor actor, Guid orderId, CancellationToken cancellationToken);
        Task<int> CompleteRefundAsync(ShopActor actor, long refundId, string reference);
        Task<Page<PlatformSeller>> PlatformSellersAsync(ShopActor actor, int page);
        Task<bool> SetSellerStatusAsync(ShopActor actor, long sellerId, SellerStatus status, string? reason);
        Task<MarketplaceModeration> ModerationAsync(ShopActor actor, int offerPage, int proposalPage);
        Task<bool> ReviewOfferAsync(ShopActor actor, long offerId, bool approve);
        Task<int?> ReviewProposalAsync(ShopActor actor, long proposalId, bool approve);
        Task<Page<PayoutQueueItemDto>> PayoutQueueAsync(ShopActor actor, int page);
        Task<int> ReviewPayoutAsync(ShopActor actor, Guid id, bool approve, string note);
        Task ReconcilePayoutAsync(ShopActor actor, Guid id, CancellationToken cancellationToken);
        Task<SalesSummary> SalesReportAsync(ShopActor actor, DateOnly start, DateOnly end);
        Task<Page<AuditEntryDto>> AuditReportAsync(ShopActor actor, DateOnly start, DateOnly end, string? action, int page);
        Task<IReadOnlyList<FailedMailDto>> FailedMailAsync(ShopActor actor);
    }
}
