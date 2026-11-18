using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Catalog;

internal sealed class StorefrontOffer
{
    public long Id { get; init; }
    public long SellerId { get; init; }
    public string SellerName { get; init; } = "";
    public long SkuId { get; init; }
    public string SkuCode { get; init; } = "";
    public string SellerSkuCode { get; init; } = "";
    public string Size { get; init; } = "";
    public string Color { get; init; } = "";
    public int ProductId { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal Price { get; init; }
    public int Available { get; init; }
    public bool Active { get; init; }

    public static IQueryable<StorefrontOffer> Query(IShopSession db)
    {
        var categories = db.Query<Category>();
        var balances = from balance in db.Query<InventoryBalance>()
                       join location in db.Query<SellerLocation>()
                           on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
                       where location.Active
                       select balance;
        return from offer in db.Query<SellerOffer>()
               join seller in db.Query<Seller>() on offer.SellerId equals seller.Id
               join sku in db.Query<Sku>() on offer.SkuId equals sku.Id
               join product in db.Query<Product>() on sku.ProductId equals product.Id
               join balance in balances on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId } into stock
               from balance in stock.DefaultIfEmpty()
               select new StorefrontOffer
               {
                   Id = offer.Id, SellerId = seller.Id, SellerName = seller.DisplayName, SkuId = sku.Id, SkuCode = sku.Code,
                   SellerSkuCode = offer.SellerSkuCode, Size = sku.Size, Color = sku.Color, ProductId = product.Id,
                   Slug = product.Slug, Name = product.Name, Price = offer.PriceVnd ?? 0,
                   Available = balance == null ? 0 : balance.OnHand - balance.Reserved,
                   Active = balance != null && offer.Status == SellerOfferStatus.Approved && offer.PriceVnd > 0
                       && seller.Status == SellerStatus.Active && sku.Active && product.Status == ProductStatus.Published
                       && categories.Any(category => category.Id == product.CategoryId && category.Active)
               };
    }
}
