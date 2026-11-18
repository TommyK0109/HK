using System.ComponentModel.DataAnnotations.Schema;
using HKShop.Contracts.Ports;
using HKShop.Domain.Sellers;

namespace HKShop.Domain.Orders;

public sealed class CustomerAddress
{
    public long Id { get; set; }
    public string CustomerId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Phone { get; set; } = "";
    public string AddressText { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long? SellerId { get; set; } = Seller.FirstPartyId;
    public string Number { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public Guid SubmissionKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? PaymentDeadline { get; set; }
    public OrderStatus Status { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
    public string Recipient { get; set; } = "";
    public string Phone { get; set; } = "";
    public string ShippingAddress { get; set; } = "";
    public decimal ShippingVnd { get; set; }
    public decimal TotalVnd { get; set; }
    public List<SellerOrder> SellerOrders { get; set; } = [];
    public List<OrderItem> Items { get; set; } = [];
    public List<OrderEvent> Events { get; set; } = [];
    public List<PaymentAttempt> PaymentAttempts { get; set; } = [];
    public Payment Payment { get; set; } = null!;
    [NotMapped]
    public Shipment? Shipment { get; set; }

    public void RequireStatus(params OrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new ShopRuleException("The current order status does not allow this action.");
    }

    public void ChangeStatus(OrderStatus status, DateTimeOffset now, string note = "")
    {
        Status = status;
        Events.Add(new OrderEvent { Status = status, OccurredAt = now, Note = note });
    }

    public void Cancel(DateTimeOffset now, string note)
    {
        RequireStatus(OrderStatus.AwaitingPayment, OrderStatus.Confirmed);
        ChangeStatus(OrderStatus.Cancelled, now, note);
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
    [NotMapped]
    public TransportParcelSummary? Transport { get; set; }

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

public sealed class StockReservation
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public long? OrderItemId { get; set; }
    public long SkuId { get; set; }
    public long OfferId { get; set; }
    public long InventoryBalanceId { get; set; }
    public int Quantity { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class Shipment
{
    public Guid SellerOrderId { get; set; }
    public Guid OrderId { get; set; }
    public string Carrier { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public DateTimeOffset DispatchedAt { get; set; }
}
