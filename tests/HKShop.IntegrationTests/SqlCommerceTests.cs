using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class SqlCommerceTests
{
    [SqlFact]
    public async Task LegacyPriceAndIdArePreserved()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261111082232_InitialProducts");
            await context.Database.ExecuteSqlRawAsync("INSERT dbo.Products (Name, Price) VALUES (N'Legacy integer', 10000), (N'Legacy fraction', 99.99);");
            await context.Database.MigrateAsync();
            var products = await context.Set<Product>().OrderBy(p => p.Id).ToListAsync();
            var skus = await context.Set<Sku>().OrderBy(s => s.ProductId).ToListAsync();
            Assert.Equal(new[] { 1, 2 }, products.Select(p => p.Id));
            Assert.Equal(new decimal?[] { 10_000m, 99.99m }, products.Select(p => p.LegacyPrice));
            Assert.All(products, p => Assert.Equal(ProductStatus.Draft, p.Status));
            Assert.Equal(10_000m, skus[0].PriceVnd);
            Assert.Null(skus[1].PriceVnd);
            Assert.Equal(2, await context.Set<StockBalance>().CountAsync());
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task ConcurrentWritersCannotReserveTheLastUnitTwice()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var sku = new Sku { Code = "ONLY-ONE", Active = true, PriceVnd = 1_000 };
            sku.Stock.Adjust(1);
            context.Add(new Product { Name = "Sản phẩm thử", Slug = "only-one", Skus = [sku] });
            await context.SaveChangesAsync();
            var store = new SqlShopStore(factory);
            async Task<bool> Reserve()
            {
                try
                {
                    return await store.Write(async db =>
                    {
                        var stock = await InventoryService.GetBalance(db, sku.Id);
                        stock.Reserve(1);
                        return true;
                    });
                }
                catch (ShopRuleException) { return false; }
            }
            var results = await Task.WhenAll(Reserve(), Reserve());
            Assert.Single(results, success => success);
            Assert.Equal(1, await context.Set<StockBalance>().AsNoTracking().Select(s => s.Reserved).SingleAsync());
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task ReviewMigrationPreservesReviewsAndAuthorsCanAddAndEditMultipleReviews()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261112084520_ProductReviews");
            var category = new Category { Name = "Danh mục thử" };
            var customer = new ShopUser { EmailConfirmed = true };
            var otherCustomer = new ShopUser { EmailConfirmed = true };
            context.AddRange(category, customer, otherCustomer);
            await context.SaveChangesAsync();

            const string slug = "review-product";
            var productId = await SqlTestDatabase.InsertLegacyProduct(context, "Sản phẩm thử", slug, category.Id);
            context.Add(new ProductImage { ProductId = productId, FileName = "test.webp" });
            context.Add(new Sku { ProductId = productId, Code = "REVIEW-SKU", Active = true, PriceVnd = 1_000 });
            await context.SaveChangesAsync();

            var catalog = new CatalogService(new SqlShopStore(factory), new UnusedImageStore(), TimeProvider.System);
            var actor = new ShopActor(customer.Id, "", true, false, false);
            var originalDate = DateTimeOffset.UtcNow.AddDays(-1);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.ProductReviews (ProductId, CustomerId, Content, Rating, CreatedAt)
                VALUES ({productId}, {customer.Id}, N'Đánh giá cũ', 4, {originalDate});
                """);
            await context.Database.MigrateAsync();
            await Assert.ThrowsAsync<ShopRuleException>(() => catalog.SubmitReview(actor, slug, "Nội dung", 5));
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET Status = 1 WHERE Id = {productId}");
            var original = Assert.Single((await catalog.Reviews(productId, actor.UserId)).Items);
            Assert.Equal((4, "Đánh giá cũ", originalDate), (original.Rating, original.Content, original.CreatedAt));
            Assert.Null(original.UpdatedAt);
            Assert.True(original.CanEdit);

            await Assert.ThrowsAsync<ShopRuleException>(() => catalog.SubmitReview(actor with { Verified = false }, slug, "Nội dung", 5));
            Assert.Equal(slug, await catalog.SubmitReview(actor, slug, "  Sản phẩm tốt  ", 5));
            var otherActor = actor with { UserId = otherCustomer.Id };
            var otherView = await catalog.Reviews(productId, otherActor.UserId);
            Assert.Equal(2, otherView.Total);
            Assert.All(otherView.Items, review => Assert.False(review.CanEdit));
            await Assert.ThrowsAsync<ShopRuleException>(() => catalog.EditReview(otherActor, slug, original.Id, "Không hợp lệ", 1));
            Assert.Equal(slug, await catalog.EditReview(actor, slug, original.Id, "  Đã cập nhật  ", 3));

            var visitor = await catalog.Reviews(productId, null);
            Assert.Collection(visitor.Items,
                latest =>
                {
                    Assert.NotEqual(original.Id, latest.Id);
                    Assert.Equal((5, "Sản phẩm tốt"), (latest.Rating, latest.Content));
                    Assert.Null(latest.UpdatedAt);
                },
                edited =>
                {
                    Assert.Equal((original.Id, 3, "Đã cập nhật", originalDate),
                        (edited.Id, edited.Rating, edited.Content, edited.CreatedAt));
                    Assert.NotNull(edited.UpdatedAt);
                });
            Assert.All(visitor.Items, review => Assert.False(review.CanEdit));
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    private sealed class UnusedImageStore : IProductImageStore
    {
        public Task<string> Save(Stream source) => throw new NotSupportedException();
    }
}

internal sealed class SqlTestDatabase(DbContextOptions<ShopDbContext> options) : IDbContextFactory<ShopDbContext>
{
    public ShopDbContext CreateDbContext() => new(options);

    public static async Task<int> InsertLegacyProduct(ShopDbContext db, string name, string slug, long? categoryId = null)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT dbo.Products (Name, Slug, Description, CategoryId, Status)
            VALUES ({name}, {slug}, N'', {categoryId}, 0);
            """);
        return await db.Database.SqlQuery<int>($"SELECT Id AS Value FROM dbo.Products WHERE Slug = {slug}").SingleAsync();
    }

    public static SqlTestDatabase Create()
    {
        var sql = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL"))
        {
            InitialCatalog = $"HKShopTests_{Guid.NewGuid():N}"
        };
        return new SqlTestDatabase(new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(sql.ConnectionString).Options);
    }
}

public class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL")))
            Skip = "Set HKSHOP_TEST_SQL for the disposable SQL Server integration database.";
    }
}
