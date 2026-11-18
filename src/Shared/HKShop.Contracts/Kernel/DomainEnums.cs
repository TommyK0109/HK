namespace HKShop.Domain.Catalog
{
    public enum ProductStatus { Draft, Published, Archived }
}

namespace HKShop.Domain.Sellers
{
    public enum SellerStatus { Pending, Active, Suspended }
    public enum SellerRole { Owner, CatalogManager, Fulfillment, Finance }
    public enum SellerMembershipStatus { Active, Revoked }
    public enum SellerOfferStatus { Pending, Approved, Rejected, Archived }
    public enum ProductProposalStatus { Pending, Approved, Rejected }
}

namespace HKShop.Domain.Promotions
{
    public enum ClaimStatus { Held, Redeemed, Released }
}

namespace HKShop.Domain.Orders
{
    public enum OrderStatus { AwaitingPayment, Confirmed, Packing, Shipped, Delivered, Cancelled, Expired, Undeliverable, PaymentFailed }
    public enum PaymentMethod { Cod, SePay, TransportAtDelivery }
    public enum PaymentStatus { Due, Pending, Paid, Failed, Expired, Cancelled }
    public enum ReservationStatus { Held, Released, Consumed }
    public enum ReturnStatus { Requested, Approved, Rejected, AwaitingRefund, Completed }
    public enum RefundStatus { Pending, Completed }
    public enum TransportStage { Packing, Packed, WaitingForShipper, Assigned, Acknowledged, PickedUp, Delivered, Returning, Returned, Cancelled }
    public enum DoorPayment { Cash, BankTransfer }
}

namespace HKShop.Domain.Settlement
{
    public enum LedgerAccountType
    {
        PaymentClearing, MarketplaceReceipts, PlatformCommission, PlatformShipping, PlatformDiscount, PlatformMerchandise,
        SellerPending, SellerAvailable, SellerPayoutInTransit, PayoutClearing, TransportExpense, ShipperPayable
    }

    public enum LedgerEventType
    {
        Capture, Sale, Commission, Refund, CommissionReversal, SettlementRelease,
        PayoutReserved, PayoutSucceeded, PayoutFailed, PayoutRejected, ShipperFeeEarned, ShipperFeePaid
    }

    public enum LedgerSide { Debit, Credit }
    public enum PayoutStatus { Requested, Processing, Paid, Failed, Rejected }
}
