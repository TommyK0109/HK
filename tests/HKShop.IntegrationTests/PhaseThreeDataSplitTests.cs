using HKShop.Catalog.Api.Infrastructure;
using HKShop.Catalog.Api.Application;
using HKShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed partial class SqlMarketplaceCheckoutTests
{
    [SqlFact]
    public async Task SplitPreservesOutstandingTwoSellerOrderAndCanBeRepeated()
    {
        var source = SqlTestDatabase.Create();
        await using var monolith = source.CreateDbContext();
        var targets = new[] { "Identity", "Catalog", "Ordering", "Payments", "Shipping", "Notification" }
            .ToDictionary(name => name, _ => SqlTestDatabase.Create().CreateDbContext());
        try
        {
            await monolith.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(source, monolith);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, false);
            var quote = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var order = await fixture.Checkout.Place(fixture.Buyer, quote.Quote);
            var connections = targets.ToDictionary(pair => $"ConnectionStrings:{pair.Key}",
                pair => pair.Value.Database.GetConnectionString());
            connections["ConnectionStrings:HKShop"] = monolith.Database.GetConnectionString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(connections).Build();

            Assert.Equal(0, await DataSplit.RunAsync(config));
            Assert.Equal(0, await DataSplit.RunAsync(config));
            await using var catalog = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
                .UseSqlServer(targets["Catalog"].Database.GetConnectionString()).Options);
            var holds = await catalog.StockHolds.Where(h => h.OrderId == order).ToListAsync();
            Assert.Equal(2, holds.Count);
            Assert.All(holds, h => Assert.True(h.SellerOrderId.HasValue));
            Assert.Equal(2, holds.Select(h => h.SellerOrderId).Distinct().Count());
            var cancelled = holds.Single(h => h.OfferId == fixture.Offer1Id);
            await CatalogStockCoordinator.ReleaseInContextAsync(catalog, order, cancelled.SellerOrderId,
                "Seller cancellation", fixture.Clock.GetUtcNow(), default);
            await catalog.SaveChangesAsync();
            catalog.ChangeTracker.Clear();
            var remaining = await catalog.StockHolds.SingleAsync(h => h.OrderId == order && h.Status == ReservationStatus.Held);
            Assert.Equal(fixture.Offer2AId, remaining.OfferId);
        }
        finally
        {
            foreach (var context in targets.Values.Append(monolith))
                await context.Database.EnsureDeletedAsync();
            foreach (var context in targets.Values)
                await context.DisposeAsync();
        }
    }
}
