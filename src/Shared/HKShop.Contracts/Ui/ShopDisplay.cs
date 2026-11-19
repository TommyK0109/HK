using System.Globalization;
using HKShop.Application;
using HKShop.Application.Reporting;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Web.Components.Shared;

public static class ShopDisplay
{
    private static readonly CultureInfo Vietnam = CultureInfo.GetCultureInfo("vi-VN");
    public static string Money(decimal amount) => $"{amount.ToString("N0", Vietnam)} ₫";
    public static string Date(DateTimeOffset value) => ShopSettings.VietnamTime(value).ToString("dd/MM/yyyy HH:mm", Vietnam);
    public static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    public static string ShortOrder(string value) => value.Length > 14 ? value[..14] : value;
    public static string AuditAction(string value) => AuditActionText.Display(value);
    public static string StockReason(string value) => value switch
    {
        "Xuất hàng" => "Dispatch", "Bỏ giữ hàng" => "Release stock", _ => value
    };
    public static string Status(OrderStatus value) => value switch
    {
        OrderStatus.AwaitingPayment => "Awaiting payment", OrderStatus.Confirmed => "Confirmed",
        OrderStatus.Packing => "Packing", OrderStatus.Shipped => "Shipped", OrderStatus.Delivered => "Delivered",
        OrderStatus.Cancelled => "Cancelled", OrderStatus.Expired => "Payment expired",
        OrderStatus.Undeliverable => "Undeliverable", OrderStatus.PaymentFailed => "Payment failed", _ => value.ToString()
    };
    public static string Status(PaymentStatus value) => value switch
    {
        PaymentStatus.Due => "Awaiting collection", PaymentStatus.Pending => "Awaiting payment", PaymentStatus.Paid => "Recorded",
        PaymentStatus.Failed => "Failed", PaymentStatus.Expired => "Expired", PaymentStatus.Cancelled => "Cancelled", _ => value.ToString()
    };
    public static string Status(ReturnStatus value) => value switch
    {
        ReturnStatus.Requested => "Pending approval", ReturnStatus.Approved => "Approved",
        ReturnStatus.Rejected => "Rejected", ReturnStatus.AwaitingRefund => "Awaiting refund",
        ReturnStatus.Completed => "Processed", _ => value.ToString()
    };
    public static string Method(PaymentMethod method) => method switch
    {
        PaymentMethod.Cod => "Cash on delivery",
        PaymentMethod.TransportAtDelivery => "Pay at delivery",
        _ => "SePay Sandbox"
    };

    public static string Status(TransportStage stage) => stage switch
    {
        TransportStage.Packing => "Preparing parcel",
        TransportStage.Packed => "Packed",
        TransportStage.WaitingForShipper => "Waiting for a HKShop shipper",
        TransportStage.Assigned => "Shipper assigned",
        TransportStage.Acknowledged => "Pickup accepted",
        TransportStage.PickedUp => "Out for delivery",
        TransportStage.Delivered => "Delivered",
        TransportStage.Returning => "Returning to seller",
        TransportStage.Returned => "Returned to seller",
        _ => "Cancelled"
    };

    public static string Status(PayoutStatus value) => value switch
    {
        PayoutStatus.Requested => "Pending approval",
        PayoutStatus.Processing => "Processing",
        PayoutStatus.Paid => "Paid out",
        PayoutStatus.Failed => "Payout failed",
        PayoutStatus.Rejected => "Rejected",
        _ => value.ToString()
    };

    public static string Status(SellerStatus value) => value switch
    {
        SellerStatus.Pending => "Pending approval",
        SellerStatus.Active => "Active",
        SellerStatus.Suspended => "Suspended",
        SellerStatus.Closed => "Closed",
        _ => value.ToString()
    };

    public static string Role(SellerRole value) => value switch
    {
        SellerRole.Owner => "Store owner",
        SellerRole.CatalogManager => "Catalog manager",
        SellerRole.Fulfillment => "Order fulfillment",
        SellerRole.Finance => "Finance",
        _ => value.ToString()
    };
}
