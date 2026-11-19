using HKShop.Contracts.Clients;
using HKShop.Domain.Orders;

namespace HKShop.Application.Checkout;

public sealed record CartLine(long OfferId, long SkuId, long SellerId, string SellerName, string Code, string Slug,
    string Name, int Quantity, int Available, decimal Price, bool Active, bool PriceChanged);
public sealed record CartView(Guid Revision, IReadOnlyList<CartLine> Lines)
{
    public decimal Estimate => Lines.Where(line => line.Active).Sum(line => line.Price * line.Quantity);
    public bool SupportsCod => Lines.Count > 0 && Lines.All(line => line.SellerId == 1);
}

public sealed record CartOffersRequest(IReadOnlyList<long> OfferIds, long? FirstPartySkuId = null);
public sealed record CartOffersResponse(IReadOnlyList<CartLine> Offers);
public sealed record CartReadRequest(string? GuestHash);
public sealed record CartChangeRequest(string? GuestHash, long? OfferId, long? SkuId, int Quantity, bool Add);

public sealed record CheckoutLineQuote(
    long OfferId,
    long SkuId,
    long SellerId,
    string SellerName,
    string Code,
    string Slug,
    string Name,
    int Quantity,
    int Available,
    decimal Price,
    decimal LineSubtotal,
    decimal SellerDiscountVnd,
    decimal PlatformDiscountVnd,
    int CommissionBps,
    decimal CommissionVnd);

public sealed record SellerOrderQuote(
    long SellerId,
    string SellerName,
    decimal SubtotalVnd,
    decimal SellerDiscountVnd,
    decimal PlatformDiscountVnd,
    decimal ShippingVnd,
    decimal CommissionVnd,
    IReadOnlyList<CheckoutLineQuote> Lines)
{
    public decimal DiscountVnd => SellerDiscountVnd + PlatformDiscountVnd;
    public decimal TotalVnd => SubtotalVnd - SellerDiscountVnd - PlatformDiscountVnd + ShippingVnd;
}

public sealed record CheckoutPreviewDto(
    CheckoutQuote Quote,
    CustomerAddressDto Address,
    IReadOnlyList<CartLine> Lines,
    IReadOnlyList<SellerOrderQuote> Sellers,
    decimal Subtotal,
    decimal Discount,
    decimal Shipping);

public sealed record QuoteCheckoutRequest(long AddressId, PaymentMethod Method, string Coupon);
