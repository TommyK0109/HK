using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;

namespace HKShop.Ordering.Api.Domain;

public sealed class ShoppingCart
{
    public long Id { get; set; }
    public string? CustomerId { get; set; }
    public string? GuestTokenHash { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CartItem> Items { get; set; } = [];
}

public sealed class CartItem
{
    public long CartId { get; set; }
    public long OfferId { get; set; }
    public long SkuId { get; set; }
    public int Quantity { get; set; }
    public decimal? SeenUnitPriceVnd { get; set; }
}

public sealed class Promotion
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public bool Active { get; set; }
    public bool IsPercentage { get; set; }
    public decimal Value { get; set; }
    public decimal MinimumVnd { get; set; }
    public int UsageCap { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    public decimal Discount(decimal subtotal, DateTimeOffset now)
    {
        if (!Active || now < StartsAt || now >= EndsAt || subtotal < MinimumVnd)
            throw new ShopRuleException("The discount code is not eligible.");
        return Math.Min(subtotal, IsPercentage ? decimal.Floor(subtotal * Value / 100) : Value);
    }
}

public sealed class PromotionClaim
{
    public long Id { get; set; }
    public long PromotionId { get; set; }
    public Guid OrderId { get; set; }
    public string CustomerId { get; set; } = "";
    public ClaimStatus Status { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long? SellerId { get; set; } = 1;
    public string Number { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public Guid SubmissionKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? PaymentDeadline { get; set; }
    public OrderStatus Status { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
    public string Recipient { get; set; } = "";
    public string Phone { get; set; } = "";
    public string ShippingAddress { get; set; } = "";
    public decimal ShippingVnd { get; set; }
    public decimal TotalVnd { get; set; }
    public string? QuotedCartSnapshotJson { get; set; }
    public Guid? QuotedCartRevision { get; set; }
    public List<SellerOrder> SellerOrders { get; set; } = [];
    public List<OrderItem> Items { get; set; } = [];
    public List<OrderEvent> Events { get; set; } = [];

    public void RequireStatus(params OrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new ShopRuleException("The current order status does not allow this action.");
    }

    public void ChangeStatus(OrderStatus status, DateTimeOffset now, string note = "")
    {
        Status = status;
        Events.Add(new OrderEvent { OrderId = Id, Status = status, OccurredAt = now, Note = note });
    }
}

public sealed class SellerOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public long SellerId { get; set; }
    public string Number { get; set; } = "";
    public OrderStatus Status { get; set; }
    public decimal SubtotalVnd { get; set; }
    public decimal SellerDiscountVnd { get; set; }
    public decimal PlatformDiscountVnd { get; set; }
    public decimal ShippingVnd { get; set; }
    public decimal TotalVnd { get; set; }
    public decimal CommissionVnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public Shipment? Shipment { get; set; }

    public void ChangeStatus(OrderStatus status, DateTimeOffset now)
    {
        Status = status;
        if (status == OrderStatus.Confirmed) ConfirmedAt ??= now;
        if (status is OrderStatus.Cancelled or OrderStatus.Expired) CancelledAt ??= now;
        if (status == OrderStatus.Delivered) DeliveredAt ??= now;
    }
}

public sealed class OrderItem
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid SellerOrderId { get; set; }
    public long SellerId { get; set; }
    public long SkuId { get; set; }
    public long OfferId { get; set; }
    public int ProductId { get; set; }
    public string SkuCode { get; set; } = "";
    public string SellerSkuCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProductAndVariantSnapshot { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPriceVnd { get; set; }
    public decimal DiscountVnd { get; set; }
    public decimal SellerDiscountVnd { get; set; }
    public decimal PlatformDiscountVnd { get; set; }
    public int CommissionBps { get; set; }
    public decimal CommissionVnd { get; set; }

    public decimal RefundFor(int previouslyAccepted, int quantity)
    {
        var net = UnitPriceVnd * Quantity - DiscountVnd;
        return decimal.Floor(net * (previouslyAccepted + quantity) / Quantity)
            - decimal.Floor(net * previouslyAccepted / Quantity);
    }
}

public sealed class OrderEvent
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public OrderStatus Status { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Note { get; set; } = "";
}

public sealed class Shipment
{
    public Guid SellerOrderId { get; set; }
    public Guid OrderId { get; set; }
    public string Carrier { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public DateTimeOffset DispatchedAt { get; set; }
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

public sealed class CheckoutRequest
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public int Status { get; set; }
    public Guid? OrderId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class OrderingOfferProjection
{
    public long OfferId { get; set; }
    public long SkuId { get; set; }
    public long SellerId { get; set; }
    public string SellerName { get; set; } = "";
    public string SellerSkuCode { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal PriceVnd { get; set; }
    public int Available { get; set; }
    public bool Active { get; set; }
}

public sealed class OrderingSellerMembershipProjection
{
    public long SellerId { get; set; }
    public string UserId { get; set; } = "";
    public SellerRole Role { get; set; }
    public SellerMembershipStatus Status { get; set; }
}
