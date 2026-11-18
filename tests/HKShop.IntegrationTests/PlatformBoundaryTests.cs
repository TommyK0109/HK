using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Application.Settlement;
using HKShop.Commerce.Application.Ports;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Email;
using HKShop.Infrastructure.Identity;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class PlatformBoundaryTests
{
    [SqlFact]
    public async Task CommerceServicesRejectStaffAndShippers()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var store = new SqlShopStore(factory);
            var clock = TimeProvider.System;

            var category = new Category { Name = "Apparel", Active = true };
            context.Add(category);
            await context.SaveChangesAsync();

            var sku = new Sku { Code = "ISO-SKU-1", Size = "M", Color = "Black", Active = true, PriceVnd = 100_000m };
            var product = new Product
            {
                CategoryId = category.Id,
                Name = "Isolation Tee",
                Slug = "isolation-tee",
                Description = "Desc",
                Skus = [sku],
                Images = [new ProductImage { FileName = "iso.webp", Alt = "Iso" }]
            };
            product.Publish(true);
            context.Add(product);
            await context.SaveChangesAsync();

            var offerId = await context.Set<SellerOffer>()
                .Where(o => o.SellerId == Seller.FirstPartyId && o.SkuId == sku.Id)
                .Select(o => o.Id)
                .SingleAsync();

            var staffActor = new ShopActor("staff-1", "staff@example.test", true, Staff: true, Admin: false, Shipper: false)
            {
                Mfa = true,
                HasEmployeeRole = true
            };
            var shipperActor = new ShopActor("shipper-1", "shipper@example.test", true, Staff: false, Admin: false, Shipper: true)
            {
                Mfa = true,
                HasEmployeeRole = true
            };

            var cart = new CartService(store, clock);
            var checkout = new CheckoutService(store, new ShopSettings(), clock);
            var catalog = new CatalogService(store, new UnusedImageStore(), clock);
            var sellers = new SellerService(store, clock);
            var sellerCatalog = new SellerCatalogService(store, clock);

            // Employees and shippers are blocked from shopping cart, checkout, product reviews, seller onboarding, and seller inventory
            foreach (var employee in new[] { staffActor, shipperActor })
            {
                await Assert.ThrowsAsync<ShopRuleException>(() =>
                    cart.SetOffer(employee.UserId, null, offerId, 1, add: true, actor: employee));
                await Assert.ThrowsAsync<ShopRuleException>(() =>
                    checkout.Quote(employee, 1, PaymentMethod.Cod, ""));
                await Assert.ThrowsAsync<ShopRuleException>(() =>
                    catalog.SubmitReview(employee, product.Slug, "Employee review", 5));
                await Assert.ThrowsAsync<ShopRuleException>(() =>
                    sellers.Register(employee, "Employee Shop", "employee-shop"));
                await Assert.ThrowsAsync<SellerAccessDeniedException>(() =>
                    sellerCatalog.CreateOffer(employee, 2, sku.Id, "EMP-1", 100_000m, 5));
            }
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task SeparatedInventoryAndInitialOfferStock_AggregatesSkuAvailabilityWithoutTouchingFirstPartyStock()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var store = new SqlShopStore(factory);
            var clock = TimeProvider.System;

            var adminUser = new ShopUser
            {
                Email = "admin@example.test", UserName = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST", NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                EmailConfirmed = true, TwoFactorEnabled = true
            };
            var sellerUser = new ShopUser
            {
                Email = "seller@example.test", UserName = "seller@example.test",
                NormalizedEmail = "SELLER@EXAMPLE.TEST", NormalizedUserName = "SELLER@EXAMPLE.TEST",
                EmailConfirmed = true, TwoFactorEnabled = true
            };
            context.AddRange(adminUser, sellerUser);
            await context.SaveChangesAsync();

            var admin = new ShopActor(adminUser.Id, adminUser.Email!, true, Staff: true, Admin: true) { Mfa = true, HasEmployeeRole = true };
            var sellerOwner = new ShopActor(sellerUser.Id, sellerUser.Email!, true, Staff: false, Admin: false) { Mfa = true };

            var catalogService = new CatalogService(store, new UnusedImageStore(), clock);
            var inventoryService = new InventoryService(store, clock);
            var sellerService = new SellerService(store, clock);
            var sellerCatalogService = new SellerCatalogService(store, clock);
            var catalogOps = new CatalogOperationsAdapter(catalogService, inventoryService);
            var commerceOps = new CommerceOperationsAdapter(
                new PromotionService(store, clock),
                new OrderService(store, clock),
                new ReturnService(store, clock),
                new PaymentService(store, new DummyPaymentGateway(), clock),
                new SettlementLedger(store, clock, new ShopSettings()),
                sellerService,
                sellerCatalogService,
                new PayoutService(store, new SimulatedPayoutGateway(store, new PayoutSimulationSettings(), clock), clock),
                new ReportingService(store));

            // 1. EOffice creates Category, Product, and SKU, and sets 1st-party stock to 5
            await catalogOps.SaveCategoryAsync(admin, 0, "Footwear", true);
            var categoryId = (await catalogOps.CategoriesAsync(false)).Single().Id;
            var productId = await catalogOps.SaveProductAsync(admin, 0, "Runner Shoe", "runner-shoe", "Lightweight shoe", categoryId);
            await catalogOps.SaveSkuAsync(admin, productId, 0, "RUN-42", "42", "White", 500_000m, true);
            var skuId = (await catalogOps.EditDetailsAsync(admin, productId)).Variants.Single().Id;
            context.Add(new ProductImage { ProductId = productId, FileName = "runner.webp", Alt = "Runner shoe" });
            await context.SaveChangesAsync();
            await catalogOps.ChangePublicationAsync(admin, productId, publish: true);

            await catalogOps.AdjustFirstPartyStockAsync(admin, skuId, delta: 5, threshold: 2, reason: "1P initial stock");

            // 2. Seller registers store, gets approved by EOffice, and creates offer with initialQuantity = 12
            var sellerId = await sellerService.Register(sellerOwner, "Sneaker Hub", "sneaker-hub");
            await commerceOps.SetSellerStatusAsync(admin, sellerId, SellerStatus.Active, "Approved");

            await sellerCatalogService.CreateOffer(sellerOwner, sellerId, skuId, "SH-RUN-42", 480_000m, initialQuantity: 12);

            var sellerOffer = await context.Set<SellerOffer>().AsNoTracking()
                .SingleAsync(o => o.SellerId == sellerId && o.SkuId == skuId);
            var sellerBalance = await context.Set<InventoryBalance>().AsNoTracking()
                .SingleAsync(b => b.OfferId == sellerOffer.Id);
            Assert.Equal(12, sellerBalance.OnHand);
            Assert.Equal(12, sellerBalance.Available);

            var initialMovement = await context.Set<OfferStockMovement>().AsNoTracking()
                .SingleAsync(m => m.InventoryBalanceId == sellerBalance.Id);
            Assert.Equal(12, initialMovement.OnHandDelta);
            Assert.Equal("Initial offer stock", initialMovement.Reason);

            // 1st-party StockBalances is untouched (still 5)
            var firstPartyStock = await context.Set<StockBalance>().AsNoTracking().SingleAsync(s => s.SkuId == skuId);
            Assert.Equal(5, firstPartyStock.OnHand);

            // Before EOffice approves the seller offer, aggregated marketplace stock only includes approved offers (1P = 5)
            var beforeApproval = (await catalogOps.EditDetailsAsync(admin, productId)).Variants.Single();
            Assert.Equal(5, beforeApproval.Available);
            Assert.Equal(5, beforeApproval.TotalOfferOnHand);
            Assert.Equal(5, beforeApproval.TotalOfferAvailable);

            // 3. EOffice approves the seller offer -> SKU total offer stock automatically aggregates 5 + 12 = 17
            await commerceOps.ReviewOfferAsync(admin, sellerOffer.Id, approve: true);

            var afterApproval = (await catalogOps.EditDetailsAsync(admin, productId)).Variants.Single();
            Assert.Equal(5, afterApproval.Available);
            Assert.Equal(17, afterApproval.TotalOfferOnHand);
            Assert.Equal(17, afterApproval.TotalOfferAvailable);

            // 4. Seller adjusts their own offer stock (+3 -> 15) -> 1P stock remains 5, aggregated offer stock becomes 20
            await sellerCatalogService.AdjustStock(sellerOwner, sellerId, sellerOffer.Id, delta: 3, reason: "Restock shipment");

            var inventoryRow = (await catalogOps.ListFirstPartyStockAsync(admin, low: false, page: 1)).Items.Single(r => r.SkuId == skuId);
            Assert.Equal(5, inventoryRow.OnHand);
            Assert.Equal(5, inventoryRow.Available);
            Assert.Equal(20, inventoryRow.TotalOfferOnHand);
            Assert.Equal(20, inventoryRow.TotalOfferAvailable);
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

    private sealed class DummyPaymentGateway : IPaymentGateway
    {
        public bool Available => true;
        public HostedCheckout BuildCheckout(string invoice, Guid order, decimal amount) => new("https://pay.example.test", []);
        public Task<GatewayOutcome?> Query(string invoice, CancellationToken cancellationToken) => Task.FromResult<GatewayOutcome?>(null);
    }
}
