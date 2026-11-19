using System.Net.Http.Headers;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Clients;
using HKShop.Domain;
using HKShop.Domain.Cart;
using HKShop.Domain.Orders;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Infrastructure.Persistence;
using HKShop.Messaging;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Payments.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed partial class SqlMarketplaceCheckoutTests
{
    [KafkaSqlFact]
    public async Task ExtractedCheckout_LastUnitIssuesOnePaymentAndPreservesLosingCart()
    {
        var source = SqlTestDatabase.Create();
        await using var db = source.CreateDbContext();
        var targets = new[] { "Catalog", "Ordering", "Payments", "Shipping", "Identity" }
            .ToDictionary(name => name, _ => SqlTestDatabase.Create().CreateDbContext());
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.tests.{Guid.NewGuid():N}.";
        var topics = new[] { "Catalog", "Ordering", "Payments" }.Select(n => prefix + n).ToArray();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = broker }).Build();
        await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 }));
        var files = Directory.CreateTempSubdirectory("hkshop-extracted-checkout-");
        try
        {
            await db.Database.MigrateAsync();
            var f = await SeedMarketplaceAsync(source, db);
            var second = await CreateUserAsync(db, "second");
            var address = new CustomerAddress { CustomerId = second.UserId, Recipient = "Buyer", Phone = "0901234567", AddressText = "124 Le Loi" };
            db.Add(address);
            const string password = "CheckoutTest_123!";
            foreach (var id in new[] { f.Buyer.UserId, second.UserId })
            {
                var user = await db.Users.SingleAsync(u => u.Id == id);
                user.PasswordHash = new PasswordHasher<ShopUser>().HashPassword(user, password);
                user.TwoFactorEnabled = false;
            }
            await db.SaveChangesAsync();
            await new InventoryService(f.Store, f.Clock).Adjust(f.Staff, f.Sku1Id, -4, 1, "Last unit");
            await f.Cart.SetOffer(f.Buyer.UserId, null, f.Offer1Id, 1, false);
            await f.Cart.SetOffer(second.UserId, null, f.Offer1Id, 1, false);
            var connections = targets.ToDictionary(p => $"ConnectionStrings:{p.Key}", p => p.Value.Database.GetConnectionString());
            connections["ConnectionStrings:HKShop"] = db.Database.GetConnectionString();
            Assert.Equal(0, await DataSplit.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(connections).Build()));

            await using var identity = Host<IdentityServiceDbContext>("Identity", []);
            await using var catalog = Host<CatalogDbContext>("Catalog", ["Ordering"]);
            await using var payments = Host<PaymentsDbContext>("Payments", ["Ordering"]);
            await using var ordering = Host<OrderingDbContext>("Ordering", ["Catalog", "Payments"], services =>
            {
                services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identity.Server.CreateHandler);
                services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalog.Server.CreateHandler);
            });
            using var identityHttp = identity.CreateClient();
            using var firstHttp = ordering.CreateClient();
            using var secondHttp = ordering.CreateClient();
            using var paymentHttp = payments.CreateClient();
            var auth = new IdentityServiceClient(identityHttp);
            var firstToken = await auth.IssueTokenAsync(new(f.Buyer.Email, password, "commerce"));
            var secondToken = await auth.IssueTokenAsync(new(second.Email, password, "commerce"));
            firstHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstToken.AccessToken);
            secondHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken.AccessToken);
            var buyers = new[] { new OrderingServiceClient(firstHttp), new OrderingServiceClient(secondHttp) };
            var quotes = await Task.WhenAll(buyers[0].QuoteAsync(new(f.AddressId, PaymentMethod.SePay, "")),
                buyers[1].QuoteAsync(new(address.Id, PaymentMethod.SePay, "")));
            var placed = await Task.WhenAll(buyers.Select((buyer, i) => buyer.PlaceOrderAsync(quotes[i].Quote)));
            await using var ord = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(connections["ConnectionStrings:Ordering"]).Options);
            await using var pay = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(connections["ConnectionStrings:Payments"]).Options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            while (!await ord.Orders.AnyAsync(o => o.Status == OrderStatus.Cancelled, deadline.Token) ||
                   !await pay.PaymentAttempts.AnyAsync(deadline.Token))
                await Task.Delay(150, deadline.Token);
            var attempt = await pay.PaymentAttempts.SingleAsync();
            var winner = Array.FindIndex(placed, result => result.OrderId == attempt.OrderId);
            Assert.InRange(winner, 0, 1);
            paymentHttp.DefaultRequestHeaders.Authorization = winner == 0 ? firstHttp.DefaultRequestHeaders.Authorization : secondHttp.DefaultRequestHeaders.Authorization;
            var form = await new PaymentsServiceClient(paymentHttp).GetCheckoutFormAsync(attempt.OrderId);
            Assert.False(string.IsNullOrWhiteSpace(form.Action));
            var losingCustomer = winner == 0 ? second.UserId : f.Buyer.UserId;
            var cart = await ord.ShoppingCarts.Include(c => c.Items).SingleAsync(c => c.CustomerId == losingCustomer);
            Assert.Equal(1, Assert.Single(cart.Items).Quantity);
            Assert.False(await db.Set<Order>().AnyAsync());
        }
        finally
        {
            await admin.DeleteTopicsAsync(topics);
            foreach (var context in targets.Values.Append(db))
                await context.Database.EnsureDeletedAsync();
            foreach (var context in targets.Values) await context.DisposeAsync();
            files.Delete(recursive: true);
        }

        WebApplicationFactory<T> Host<T>(string service, string[] subscriptions, Action<IServiceCollection>? configure = null) where T : class =>
            new WebApplicationFactory<T>().WithWebHostBuilder(web => web
                .UseSetting($"ConnectionStrings:{service}", targets[service].Database.GetConnectionString())
                .UseSetting("Jwt:SigningKey", "isolated-phase-3-integration-test-signing-key")
                .UseSetting("Shop:KeysPath", Path.Combine(files.FullName, "keys"))
                .UseSetting("Shop:MediaPath", Path.Combine(files.FullName, "media"))
                .UseSetting("Shop:PublicOrigin", "https://shop.example.test")
                .UseSetting("SePay:MerchantId", "test").UseSetting("SePay:MerchantSecret", "test")
                .UseSetting("SePay:IpnSecret", "test")
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton(new OutboxRelayOptions(service == "Identity" ? "" : broker, prefix + service, service));
                    services.AddSingleton(new InboxConsumerOptions(broker, prefix + service, service, subscriptions.Select(s => prefix + s).ToArray()));
                    configure?.Invoke(services);
                }));
    }
}
