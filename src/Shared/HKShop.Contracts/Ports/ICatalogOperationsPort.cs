using HKShop.Application;
using HKShop.Domain.Catalog;

namespace HKShop.Application.Catalog
{
    public sealed record SkuDisplay(
        long Id,
        string Code,
        string Size,
        string Color,
        decimal? Price,
        bool Active,
        int Available,
        int TotalOfferOnHand = 0,
        int TotalOfferAvailable = 0);

    public sealed record StockRow(
        long SkuId,
        string Code,
        string Product,
        int OnHand,
        int Reserved,
        int LowThreshold,
        int TotalOfferOnHand = 0,
        int TotalOfferAvailable = 0)
    {
        public int Available => OnHand - Reserved;
    }

    public sealed record CatalogCategoryDto(long Id, string Name, bool Active);
    public sealed record CatalogProductDto(int Id, long? CategoryId, string Name, string Slug, string Description, decimal? LegacyPrice, ProductStatus Status);
    public sealed record CatalogImageDto(long Id, int ProductId, string FileName, string Alt);
    public sealed record CatalogEditDetailsDto(CatalogProductDto Product, IReadOnlyList<SkuDisplay> Variants, IReadOnlyList<CatalogImageDto> Images);
    public sealed record StockMovementDto(long Id, long SkuId, Guid? OrderId, int OnHandDelta, int ReservedDelta, string Reason, string? ActorId, DateTimeOffset OccurredAt);
}

namespace HKShop.Contracts.Ports
{
    using HKShop.Application;
    using HKShop.Application.Catalog;

    public interface ICatalogOperationsPort
    {
        Task<IReadOnlyList<CatalogCategoryDto>> CategoriesAsync(bool activeOnly = true);
        Task<Page<CatalogProductDto>> ProductsAsync(ShopActor actor, int page);
        Task<CatalogEditDetailsDto> EditDetailsAsync(ShopActor actor, int id);
        Task<int> SaveCategoryAsync(ShopActor actor, long id, string name, bool active);
        Task<int> SaveProductAsync(ShopActor actor, int id, string name, string slug, string description, long? categoryId);
        Task<long> SaveSkuAsync(ShopActor actor, int productId, long skuId, string code, string size, string color, decimal price, bool active);
        Task AddImageAsync(ShopActor actor, int productId, string alt, Stream file);
        Task<int> ChangePublicationAsync(ShopActor actor, int id, bool publish);
        Task<Page<StockRow>> ListFirstPartyStockAsync(ShopActor actor, bool low, int page = 1);
        Task<IReadOnlyList<StockMovementDto>> FirstPartyHistoryAsync(ShopActor actor, long skuId);
        Task<int> AdjustFirstPartyStockAsync(ShopActor actor, long skuId, int delta, int threshold, string reason);
    }
}
