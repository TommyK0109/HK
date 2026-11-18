namespace HKShop.Domain.Orders;

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

public sealed class ReturnRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid SellerOrderId { get; set; }
    public ReturnStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string StaffNote { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public List<ReturnItem> Items { get; set; } = [];
}

public sealed class ReturnItem
{
    public long Id { get; set; }
    public Guid ReturnId { get; set; }
    public Guid SellerOrderId { get; set; }
    public long OrderItemId { get; set; }
    public int Requested { get; set; }
    public int Accepted { get; set; }
    public int Restocked { get; set; }
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
