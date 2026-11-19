using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Payments.Api.Domain;

public sealed class Payment
{
    public Guid OrderId { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal AmountVnd { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? ProviderTransaction { get; set; }
    public string CollectionReference { get; set; } = "";
    public DateTimeOffset? NextQueryAt { get; set; }
    public string Issue { get; set; } = "";
}

public sealed class PaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid? SellerOrderId { get; set; }
    public int AttemptNumber { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal AmountVnd { get; set; }
    public decimal CapturedAmountVnd { get; set; }
    public string? ProviderInvoice { get; set; }
    public string? ProviderTransaction { get; set; }
    public bool AppliedToOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? NextQueryAt { get; set; }
    public string Issue { get; set; } = "";
}

public sealed class PaymentEvent
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public string EventType { get; set; } = "";
    public string EventKey { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class Refund
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid? PaymentAttemptId { get; set; }
    public Guid? SellerOrderId { get; set; }
    public Guid? ReturnId { get; set; }
    public string BusinessKey { get; set; } = "";
    public decimal AmountVnd { get; set; }
    public decimal ShippingVnd { get; set; }
    public RefundStatus Status { get; set; }
    public bool Simulated { get; set; }
    public string Reference { get; set; } = "";
    public string ProviderReference { get; set; } = "";
    public string Issue { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<RefundItem> Items { get; set; } = [];
}

public sealed class RefundItem
{
    public long RefundId { get; set; }
    public long OrderItemId { get; set; }
    public Guid SellerOrderId { get; set; }
    public int Quantity { get; set; }
    public decimal AmountVnd { get; set; }
    public decimal CommissionReversalVnd { get; set; }
    public decimal PlatformDiscountReversalVnd { get; set; }
}

public sealed class LedgerAccount
{
    public long Id { get; set; }
    public long? SellerId { get; set; }
    public LedgerAccountType Type { get; set; }
}

public sealed class LedgerJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SellerOrderId { get; set; }
    public Guid? PaymentAttemptId { get; set; }
    public Guid? PayoutId { get; set; }
    public long? RefundId { get; set; }
    public string BusinessKey { get; set; } = "";
    public LedgerEventType Type { get; set; }
    public DateTimeOffset PostedAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid JournalId { get; set; }
    public long AccountId { get; set; }
    public decimal AmountVnd { get; set; }
    public LedgerSide Side { get; set; }
}

public sealed class Payout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SellerId { get; set; }
    public Guid SubmissionKey { get; set; }
    public decimal AmountVnd { get; set; }
    public PayoutStatus Status { get; set; }
    public string ProviderReference { get; set; } = "";
    public string Issue { get; set; } = "";
    public string ReviewNote { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class SimulatedPayoutTransfer
{
    public Guid PayoutId { get; set; }
    public string ProviderReference { get; set; } = "";
    public decimal AmountVnd { get; set; }
    public bool Succeeded { get; set; }
    public DateTimeOffset ConfirmAt { get; set; }
}

public sealed class PaymentOrderProjection
{
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public OrderStatus Status { get; set; }
    public DateTimeOffset? PaymentDeadline { get; set; }
    public decimal TotalVnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentSellerOrderProjection
{
    public Guid SellerOrderId { get; set; }
    public Guid OrderId { get; set; }
    public long SellerId { get; set; }
    public string SellerOrderNumber { get; set; } = "";
    public OrderStatus Status { get; set; }
    public decimal SubtotalVnd { get; set; }
    public decimal SellerDiscountVnd { get; set; }
    public decimal PlatformDiscountVnd { get; set; }
    public decimal ShippingVnd { get; set; }
    public decimal TotalVnd { get; set; }
    public decimal CommissionVnd { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public sealed class PaymentSellerProjection
{
    public long SellerId { get; set; }
    public string DisplayName { get; set; } = "";
    public SellerStatus Status { get; set; }
}

public sealed class PaymentSellerMembershipProjection
{
    public long SellerId { get; set; }
    public string UserId { get; set; } = "";
    public SellerRole Role { get; set; }
    public SellerMembershipStatus Status { get; set; }
}
