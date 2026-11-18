using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class SqlMarketplaceCatalogTests
{
    [SqlFact]
    public async Task DraftCatalogImportsWithImagesAreBrowsableEvenWithSeededPrices()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var category = new Category { Name = "Imported clothing" };
            context.Add(category);
            await context.SaveChangesAsync();

            Product Entry(string slug, string name, string sku) => new()
            {
                Slug = slug, Name = name, CategoryId = category.Id,
                Images = [new ProductImage { FileName = "sample.jpg", Alt = name }],
                Skus = [new Sku { Code = sku, PriceVnd = 50_000, Active = true }]
            };
            var sale = Entry("regular-sale", "For sale", "SALE-1");
            sale.Publish(category.Active);
            var imported = Entry("myntra-for-browsing", "Imported shirt", "MYNTRA-1");
            imported.Skus[0].PriceVnd = 45_000;
            var archived = Entry("abo-archived", "Archived import", "ABO-1");
            archived.Archive();
            context.AddRange(sale, imported, archived, Entry("unlisted-draft", "Ordinary draft", "DRAFT-1"));
            await context.SaveChangesAsync();

            var catalog = new CatalogService(new SqlShopStore(factory), new UnusedImageStore(), TimeProvider.System);
            var all = await catalog.Browse(null, category.Id, "price-asc", mode: CatalogBrowseMode.All);
            Assert.Equal(new[] { imported.Slug, sale.Slug }, all.Items.Select(item => item.Slug));
            Assert.Equal(45_000m, all.Items[0].Price);
            Assert.True(all.Items[0].BrowseOnly);
            Assert.False(all.Items[1].BrowseOnly);
            var imports = await catalog.Browse("MYNTRA-1", category.Id, null, mode: CatalogBrowseMode.Imports);
            Assert.Equal("sample.jpg", Assert.Single(imports.Items).FileName);
            Assert.Equal(sale.Slug, Assert.Single((await catalog.Browse(null, category.Id, null)).Items).Slug);
            Assert.Null(await catalog.Details(imported.Slug));
            var preview = Assert.IsType<CatalogDetails>(await catalog.Details(imported.Slug, includeImports: true));
            Assert.True(preview.BrowseOnly);
            Assert.Equal(45_000m, preview.CatalogPriceVnd);
            Assert.Empty(preview.Offers);
            Assert.Null(await catalog.Details(archived.Slug, includeImports: true));
            Assert.Null(await catalog.Details("unlisted-draft", includeImports: true));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task MultipleSellersCanListSameVariantWithIndependentPricingAndStock()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var store = new SqlShopStore(factory);

            // 1. Tạo Category và Product chung với 1 SKU
            var category = new Category { Name = "Thời trang Nam", Active = true };
            context.Add(category);
            await context.SaveChangesAsync();

            var sku = new Sku { Code = "TEE-BASIC-L", Size = "L", Color = "Trắng", Active = true, PriceVnd = 150_000m };
            var product = new Product
            {
                CategoryId = category.Id,
                Name = "Áo thun Basic Nam",
                Slug = "ao-thun-basic-nam",
                Description = "Chất liệu cotton thoáng mát",
                Skus = [sku],
                Images = [new ProductImage { FileName = "tee.webp", Alt = "Áo thun" }]
            };
            product.Publish(category.Active);
            context.Add(product);
            await context.SaveChangesAsync();

            // 2. Tạo Seller A và Seller B
            var sellerA = new Seller { Slug = "shop-a", DisplayName = "Shop A", Status = SellerStatus.Active };
            var sellerB = new Seller { Slug = "shop-b", DisplayName = "Shop B", Status = SellerStatus.Active };
            context.AddRange(sellerA, sellerB);
            await context.SaveChangesAsync();

            var locA = new SellerLocation { SellerId = sellerA.Id, Name = "Kho Hà Nội", Active = true };
            var locB = new SellerLocation { SellerId = sellerB.Id, Name = "Kho TP.HCM", Active = true };
            context.AddRange(locA, locB);
            await context.SaveChangesAsync();

            // 3. Seller A chào bán với giá 200.000₫, tồn kho 10
            var offerA = new SellerOffer
            {
                SellerId = sellerA.Id,
                SkuId = sku.Id,
                SellerSkuCode = "A-TEE-WHITE-L",
                PriceVnd = 200_000m,
                Status = SellerOfferStatus.Approved
            };
            context.Add(offerA);
            await context.SaveChangesAsync();
            context.Add(new InventoryBalance { SellerId = sellerA.Id, OfferId = offerA.Id, LocationId = locA.Id, OnHand = 10, Reserved = 0 });

            // 4. Seller B chào bán với giá 180.000₫, tồn kho 5
            var offerB = new SellerOffer
            {
                SellerId = sellerB.Id,
                SkuId = sku.Id,
                SellerSkuCode = "B-TEE-WHITE-L",
                PriceVnd = 180_000m,
                Status = SellerOfferStatus.Approved
            };
            context.Add(offerB);
            await context.SaveChangesAsync();
            context.Add(new InventoryBalance { SellerId = sellerB.Id, OfferId = offerB.Id, LocationId = locB.Id, OnHand = 5, Reserved = 0 });
            await context.SaveChangesAsync();

            // 5. Kiểm tra CatalogService.Details: Cả 2 offer đều Active và ưu tiên giá thấp hơn
            var catalogService = new CatalogService(store, new UnusedImageStore(), TimeProvider.System);
            var details = await catalogService.Details(product.Slug);
            Assert.NotNull(details);
            Assert.Equal(2, details.Offers.Count);

            // Ưu tiên offer giá thấp hơn lên đầu (Seller B: 180k)
            Assert.Equal(offerB.Id, details.Offers[0].Id);
            Assert.Equal("Shop B", details.Offers[0].SellerName);
            Assert.Equal(180_000m, details.Offers[0].Price);
            Assert.Equal(5, details.Offers[0].Available);

            // Offer của Seller A xếp thứ hai (200k)
            Assert.Equal(offerA.Id, details.Offers[1].Id);
            Assert.Equal("Shop A", details.Offers[1].SellerName);
            Assert.Equal(200_000m, details.Offers[1].Price);
            Assert.Equal(10, details.Offers[1].Available);

            // 6. Đình chỉ Seller B -> Offer B tự động biến mất khỏi Storefront
            sellerB.Status = SellerStatus.Suspended;
            await context.SaveChangesAsync();

            var updatedDetails = await catalogService.Details(product.Slug);
            Assert.NotNull(updatedDetails);
            var single = Assert.Single(updatedDetails.Offers);
            Assert.Equal(offerA.Id, single.Id);
            Assert.Equal("Shop A", single.SellerName);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private sealed class UnusedImageStore : IProductImageStore
    {
        public Task<string> Save(Stream source) => throw new NotSupportedException();
    }
}
