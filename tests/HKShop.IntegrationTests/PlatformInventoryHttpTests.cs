using System.Net;
using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class PlatformInventoryHttpTests
{
    [SqlFact]
    public async Task InventoryFormsKeepFirstPartyAndSellerProductsSeparate()
    {
        await using var db = SqlTestDatabase.Create().CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
            using var app = PlatformHttpTestHost.Create(db.Database.GetConnectionString());
            var (admin, owner) = await CreateAccountsAsync(app.Services);
            using var eoffice = await app.CreateBrowserAsync(admin.Id);
            using var commerce = await app.CreateBrowserAsync(owner.Id);

            var (product, sku, alternateSku, firstPartySku, seller) = await SeedCatalogAsync(db, owner);
            var catalogPage = $"/seller/{seller.Id}/catalog";
            var offersAction = $"/api/commerce/seller/{seller.Id}/catalog/offers";
            const string inventoryPage = "/eoffice/inventory";
            const string marketplacePage = "/eoffice/marketplace";

            Assert.Contains("action=\"/api/eoffice/inventory/stock\"", await InventoryHtmlAsync());
            await AdjustFirstPartyAsync("5", "2");
            var sellerCatalog = await commerce.GetStringAsync(catalogPage);
            Assert.Contains($"action=\"{offersAction}\"", sellerCatalog);
            Assert.Contains("name=\"quantity\"", sellerCatalog);
            Assert.DoesNotContain(firstPartySku.Code, sellerCatalog);
            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, offersAction, catalogPage,
                OfferForm(firstPartySku.Id, "FORBIDDEN", "1")), catalogPage);
            Assert.Contains("This variant is not available for listing.", await commerce.GetStringAsync(catalogPage));
            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, offersAction, catalogPage,
                OfferForm(sku.Id, "SELLER-POSITIVE", "12")), catalogPage);

            var offer = await db.Set<SellerOffer>().AsNoTracking()
                .SingleAsync(o => o.SellerId == seller.Id && o.SkuId == sku.Id);
            Assert.Equal(SellerOfferStatus.Pending, offer.Status);
            var initialStock = await SellerStockAsync();
            Assert.Equal((12, 0), (initialStock.OnHand, initialStock.Reserved));
            var movement = await db.Set<OfferStockMovement>().AsNoTracking()
                .SingleAsync(m => m.InventoryBalanceId == initialStock.Id);
            Assert.Equal((12, 0, owner.Id), (movement.OnHandDelta, movement.ReservedDelta, movement.ActorId));
            Assert.Contains("Marketplace on hand 5 · Marketplace available 5", await InventoryHtmlAsync());

            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, offersAction, catalogPage,
                OfferForm(alternateSku.Id, "SELLER-NEGATIVE", "-1")), catalogPage);
            Assert.Contains("Initial stock quantity cannot be negative.", await commerce.GetStringAsync(catalogPage));
            Assert.False(await db.Set<SellerOffer>().AnyAsync(o => o.SellerId == seller.Id && o.SkuId == alternateSku.Id));

            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, offersAction, catalogPage,
                OfferForm(alternateSku.Id, "SELLER-ZERO", "0")), catalogPage);
            var zeroOffer = await db.Set<SellerOffer>().AsNoTracking()
                .SingleAsync(o => o.SellerId == seller.Id && o.SkuId == alternateSku.Id);
            var zeroStock = await db.Set<InventoryBalance>().AsNoTracking().SingleAsync(b => b.OfferId == zeroOffer.Id);
            Assert.Equal((0, 0), (zeroStock.OnHand, zeroStock.Reserved));
            Assert.False(await db.Set<OfferStockMovement>().AnyAsync(m => m.InventoryBalanceId == zeroStock.Id));

            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(eoffice,
                $"/api/eoffice/marketplace/offers/{offer.Id}", marketplacePage, new() { ["decision"] = "approve" }),
                marketplacePage);
            var stockAction = $"/api/commerce/inventory/seller/{seller.Id}/offers/{offer.Id}/stock";
            Assert.Contains($"action=\"{stockAction}\"", await commerce.GetStringAsync(catalogPage));
            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, stockAction, catalogPage,
                new() { ["delta"] = "3", ["reason"] = "Seller restock" }), $"{catalogPage}?history={offer.Id}#stock-history");
            Assert.Equal(5, (await FirstPartyStockAsync()).OnHand);
            Assert.Equal(15, (await SellerStockAsync()).OnHand);
            Assert.Contains("Marketplace on hand 5 · Marketplace available 5", await InventoryHtmlAsync());

            // Seed reserved units to distinguish available from on-hand stock in the rendered totals.
            var reservedStock = await db.Set<InventoryBalance>().SingleAsync(b => b.OfferId == offer.Id);
            reservedStock.Reserve(4);
            await db.SaveChangesAsync();

            await AdjustFirstPartyAsync("2", "4");
            var firstParty = await FirstPartyStockAsync();
            Assert.Equal((7, 4), (firstParty.OnHand, firstParty.LowThreshold));
            var thirdParty = await SellerStockAsync();
            Assert.Equal((15, 4), (thirdParty.OnHand, thirdParty.Reserved));
            Assert.Contains("1P On hand 7 · 1P Reserved 0 · Marketplace on hand 7 · Marketplace available 7",
                await InventoryHtmlAsync());
            var editPage = WebUtility.HtmlDecode(await eoffice.GetStringAsync($"/eoffice/products/{product.Id}"));
            Assert.Contains($"{sku.Code} · Active · Available 0 · Marketplace available 11 (on hand 15)", editPage);

            await AssertRedirectAsync(PlatformHttpTestHost.PostFormAsync(commerce, $"{offersAction}/{offer.Id}/archive",
                catalogPage, new()), catalogPage);
            Assert.Equal(SellerOfferStatus.Archived, await db.Set<SellerOffer>().Where(o => o.Id == offer.Id)
                .Select(o => o.Status).SingleAsync());
            Assert.Contains("Marketplace on hand 7 · Marketplace available 7", await InventoryHtmlAsync());

            Task<InventoryBalance> SellerStockAsync() =>
                db.Set<InventoryBalance>().AsNoTracking().SingleAsync(b => b.OfferId == offer.Id);

            Task<StockBalance> FirstPartyStockAsync() =>
                db.Set<StockBalance>().AsNoTracking().SingleAsync(b => b.SkuId == firstPartySku.Id);

            async Task<string> InventoryHtmlAsync() => WebUtility.HtmlDecode(await eoffice.GetStringAsync(inventoryPage));

            Task AdjustFirstPartyAsync(string delta, string threshold) => AssertRedirectAsync(
                PlatformHttpTestHost.PostFormAsync(eoffice, "/api/eoffice/inventory/stock", inventoryPage,
                    new() { ["skuId"] = $"{firstPartySku.Id}", ["delta"] = delta, ["threshold"] = threshold, ["reason"] = "HKShop restock" }),
                $"{inventoryPage}?sku={firstPartySku.Id}");
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static Dictionary<string, string> OfferForm(long skuId, string code, string quantity) => new()
    {
        ["skuId"] = $"{skuId}", ["code"] = code, ["price"] = "95000", ["quantity"] = quantity
    };

    private static async Task AssertRedirectAsync(Task<HttpResponseMessage> request, string location)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    private static async Task<(ShopUser Admin, ShopUser Owner)> CreateAccountsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ShopUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var admin = Account("inventory-admin@example.test");
        var owner = Account("inventory-owner@example.test");
        Assert.True((await users.CreateAsync(admin)).Succeeded);
        Assert.True((await users.CreateAsync(owner)).Succeeded);
        Assert.True((await roles.CreateAsync(new IdentityRole("Admin"))).Succeeded);
        Assert.True((await users.AddToRoleAsync(admin, "Admin")).Succeeded);
        return (admin, owner);

        static ShopUser Account(string email) => new()
        {
            UserName = email, Email = email, EmailConfirmed = true, TwoFactorEnabled = true
        };
    }

    private static async Task<(Product Product, Sku Sku, Sku AlternateSku, Sku FirstPartySku, Seller Seller)> SeedCatalogAsync(
        ShopDbContext db, ShopUser owner)
    {
        var category = new Category { Name = "Inventory test apparel" };
        var seller = new Seller
        {
            Slug = "inventory-test-seller", DisplayName = "Inventory test seller", Status = SellerStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(category, seller);
        await db.SaveChangesAsync();

        var sku = new Sku { Code = "INVENTORY-M", Size = "M", Color = "Black", PriceVnd = 100_000m, Active = true };
        var alternateSku = new Sku { Code = "INVENTORY-L", Size = "L", Color = "Black", PriceVnd = 100_000m, Active = true };
        var product = new Product
        {
            SellerId = seller.Id, CategoryId = category.Id, Name = "Inventory test shirt", Slug = "inventory-test-shirt",
            Skus = [sku, alternateSku], Images = [new ProductImage { FileName = "inventory.webp", Alt = "Shirt" }]
        };
        product.Publish(category.Active);
        var firstPartySku = new Sku { Code = "HK-INVENTORY", PriceVnd = 100_000m, Active = true };
        var firstParty = new Product
        {
            CategoryId = category.Id, Name = "HKShop shirt", Slug = "inventory-hkshop-shirt",
            Skus = [firstPartySku], Images = [new ProductImage { FileName = "inventory.webp", Alt = "Shirt" }]
        };
        firstParty.Publish(category.Active);
        db.AddRange(product, firstParty);
        db.Add(new SellerMembership
        {
            SellerId = seller.Id, UserId = owner.Id, Email = owner.Email!, Role = SellerRole.Owner,
            Status = SellerMembershipStatus.Active
        });
        await db.SaveChangesAsync();
        return (product, sku, alternateSku, firstPartySku, seller);
    }
}
