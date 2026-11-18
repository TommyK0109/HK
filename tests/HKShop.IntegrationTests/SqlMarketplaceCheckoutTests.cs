using System.Net;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Cart;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Kafka;
using HKShop.Infrastructure.Persistence;
using HKShop.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class KafkaSqlFactAttribute : SqlFactAttribute
{
    public KafkaSqlFactAttribute()
    {
        if (Skip is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")))
            Skip = "Set HKSHOP_TEST_KAFKA to a disposable Kafka broker.";
    }
}

public sealed class SqlMarketplaceCheckoutTests
{
    [SqlFact]
    public async Task ExpiredQueuedCheckoutCannotPlaceAnOrderAfterDelivery()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var queue = new CheckoutRequests(fixture.Store, fixture.Checkout, factory, fixture.Clock);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, false);
            var quote = (await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, "")).Quote;
            var request = await queue.Submit(fixture.Buyer, quote);
            Assert.Equal(CheckoutRequestStatus.Pending, request.Status);

            fixture.Clock.UtcNow = quote.ExpiresAt;
            Assert.Equal(1, await queue.Expire(CancellationToken.None));
            await queue.Process(request.RequestId);
            var result = await queue.Submit(fixture.Buyer, quote);
            Assert.Equal(CheckoutRequestStatus.Rejected, result.Status);
            Assert.Equal(CheckoutService.ExpiredQuoteMessage, result.Error);
            Assert.Empty(await context.Set<Order>().ToListAsync());
            Assert.Empty(await context.Set<PaymentAttempt>().ToListAsync());
            Assert.Equal(1, await context.Set<CartItem>().CountAsync());
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task QueuedCheckoutRejectsAFormerCustomerAfterAnEmployeeRoleIsAssigned()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var queue = new CheckoutRequests(fixture.Store, fixture.Checkout, factory, fixture.Clock);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, false);

            foreach (var roleName in new[] { "Admin", "StoreStaff", "Shipper" })
            {
                var quote = (await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, "")).Quote;
                var request = await queue.Submit(fixture.Buyer, quote);
                Assert.Equal(CheckoutRequestStatus.Pending, request.Status);

                var role = await context.Roles.SingleOrDefaultAsync(r => r.Name == roleName);
                if (role is null)
                {
                    role = new IdentityRole(roleName) { NormalizedName = roleName.ToUpperInvariant() };
                    context.Roles.Add(role);
                }
                var assignment = new IdentityUserRole<string> { UserId = fixture.Buyer.UserId, RoleId = role.Id };
                context.Add(assignment);
                await context.SaveChangesAsync();

                await queue.Process(request.RequestId);

                var result = await queue.Get(fixture.Buyer, request.RequestId);
                Assert.NotNull(result);
                Assert.Equal(CheckoutRequestStatus.Rejected, result.Status);
                Assert.Equal("Employee and shipper accounts cannot use shopping features.", result.Error);
                Assert.Null(result.OrderId);
                Assert.False(await context.Set<Order>().AnyAsync());
                Assert.False(await context.Set<Payment>().AnyAsync());
                Assert.False(await context.Set<PaymentAttempt>().AnyAsync());
                Assert.False(await context.Set<StockReservation>().AnyAsync());

                var balance = await context.Set<InventoryBalance>().AsNoTracking().SingleAsync(b => b.OfferId == fixture.Offer1Id);
                Assert.Equal(5, balance.OnHand);
                Assert.Equal(0, balance.Reserved);
                var cart = await fixture.Cart.Get(fixture.Buyer.UserId, null);
                var line = Assert.Single(cart.Lines);
                Assert.Equal(fixture.Offer1Id, line.OfferId);
                Assert.Equal(1, line.Quantity);

                context.Remove(assignment);
                await context.SaveChangesAsync();
            }
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [KafkaSqlFact]
    public async Task TwoQueuedBuyersOnlyOneCanOpenSePayForTheLastUnit()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        var topic = $"hkshop.checkout.tests.{Guid.NewGuid():N}";
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = broker }).Build();
        var topicCreated = false;
        var files = Directory.CreateTempSubdirectory("hkshop-checkout-");
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var other = await CreateUserAsync(context, "other");
            var address = new CustomerAddress
            {
                CustomerId = other.UserId, Recipient = "Other Buyer", Phone = "0901234567", AddressText = "124 Le Loi"
            };
            context.Add(address);
            const string password = "CheckoutTest_123!";
            var hasher = new PasswordHasher<ShopUser>();
            foreach (var id in new[] { fixture.Buyer.UserId, other.UserId })
            {
                var user = await context.Users.SingleAsync(u => u.Id == id);
                user.PasswordHash = hasher.HashPassword(user, password);
                user.TwoFactorEnabled = false;
            }
            await context.SaveChangesAsync();
            await new InventoryService(fixture.Store, fixture.Clock)
                .Adjust(fixture.Staff, fixture.Sku1Id, -4, 1, "Leave the final unit");
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, false);
            await fixture.Cart.SetOffer(other.UserId, null, fixture.Offer1Id, 1, false);

            using var app = new WebApplicationFactory<ShopFailureFilter>().WithWebHostBuilder(web => web
                .UseSetting("ConnectionStrings:HKShop", context.Database.GetConnectionString())
                .UseSetting("Kafka:BootstrapServers", broker).UseSetting("Kafka:CheckoutTopic", topic)
                .UseSetting("Kafka:CheckoutGroupId", topic)
                .UseSetting("Shop:KeysPath", Path.Combine(files.FullName, "keys"))
                .UseSetting("Shop:MediaPath", Path.Combine(files.FullName, "media"))
                .UseSetting("Shop:PublicOrigin", "https://localhost")
                .UseSetting("SePay:MerchantId", "test").UseSetting("SePay:MerchantSecret", "test")
                .UseSetting("SePay:IpnSecret", "test"));
            var clientOptions = new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
            };
            using var first = app.CreateClient(clientOptions);
            using var second = app.CreateClient(clientOptions);
            await SignIn(first, fixture.Buyer.Email, password);
            await SignIn(second, other.Email, password);

            var firstQuote = await first.GetStringAsync($"/checkout?address={fixture.AddressId}&method=SePay");
            var secondQuote = await second.GetStringAsync($"/checkout?address={address.Id}&method=SePay");
            var clients = new[] { first, second };
            var quotes = new[] { firstQuote, secondQuote };
            var submissions = await Task.WhenAll(SubmitCheckout(first, firstQuote), SubmitCheckout(second, secondQuote),
                SubmitCheckout(first, firstQuote));
            foreach (var response in submissions) Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal(submissions[0].Headers.Location, submissions[2].Headers.Location);
            var statusPaths = submissions.Take(2).Select(response => response.Headers.Location!.ToString()).ToArray();
            Assert.All(statusPaths, path => Assert.StartsWith("/checkout/requests/", path));
            Assert.Contains("Your order is being processed", await first.GetStringAsync(statusPaths[0]));
            Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(statusPaths[0])).StatusCode);
            Assert.Equal(2, await context.Set<CheckoutRequest>().CountAsync(r => r.PublishedAt == null));

            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
            topicCreated = true;
            await WaitUntil(async () => await context.Set<CheckoutRequest>()
                .CountAsync(r => r.Status != CheckoutRequestStatus.Pending) == 2);

            var order = await context.Set<Order>().SingleAsync();
            var requests = await context.Set<CheckoutRequest>().AsNoTracking().ToListAsync();
            var winner = Assert.Single(requests, r => r.Status == CheckoutRequestStatus.Placed);
            var loser = Assert.Single(requests, r => r.Status == CheckoutRequestStatus.Rejected);
            Assert.Contains("out-of-stock", loser.Error);
            var winnerIndex = order.CustomerId == fixture.Buyer.UserId ? 0 : 1;
            var loserIndex = 1 - winnerIndex;
            var orderPath = $"/orders/{order.Id:D}";
            var status = await clients[winnerIndex].GetAsync(statusPaths[winnerIndex]);
            Assert.Equal(HttpStatusCode.Redirect, status.StatusCode);
            Assert.Equal(orderPath, new Uri(clients[winnerIndex].BaseAddress!, status.Headers.Location!).AbsolutePath);
            var rejected = await clients[loserIndex].GetStringAsync(statusPaths[loserIndex]);
            Assert.Contains("out-of-stock", rejected);
            Assert.Equal(HttpStatusCode.NotFound, (await clients[loserIndex].GetAsync(orderPath)).StatusCode);
            Assert.Equal(loser.CustomerId, await (from line in context.Set<CartItem>()
                join cart in context.Set<ShoppingCart>() on line.CartId equals cart.Id
                select cart.CustomerId).SingleAsync());
            Assert.Equal(1, await context.Set<StockReservation>().CountAsync(r => r.Status == ReservationStatus.Held));
            Assert.Equal(1, await context.Set<InventoryBalance>().Where(b => b.OfferId == fixture.Offer1Id)
                .Select(b => b.Reserved).SingleAsync());

            var replay = await SubmitCheckout(clients[winnerIndex], quotes[winnerIndex]);
            Assert.Equal(orderPath, replay.Headers.Location?.OriginalString);
            using var producer = new ProducerBuilder<Null, string>(new ProducerConfig { BootstrapServers = broker }).Build();
            var delivery = await producer.ProduceAsync(topic, new Message<Null, string> { Value = winner.Id.ToString("D") });
            await WaitUntil(async () =>
            {
                var offsets = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(topic, [delivery.TopicPartition])]);
                return offsets.Single().Partitions.Single().Offset.Value > delivery.Offset.Value;
            });
            Assert.Equal(1, await context.Set<Order>().CountAsync());
            Assert.Equal(1, await context.Set<PaymentAttempt>().CountAsync());
            Assert.Equal(1, await context.Set<StockReservation>().CountAsync());
            Assert.Contains("pay-sandbox.sepay.vn", await clients[winnerIndex].GetStringAsync(orderPath));
        }
        finally
        {
            if (topicCreated) await admin.DeleteTopicsAsync([topic]);
            await context.Database.EnsureDeletedAsync();
            files.Delete(recursive: true);
        }
    }

    private static async Task SignIn(HttpClient client, string email, string password)
    {
        var form = await client.GetStringAsync("/account/login");
        using var login = await client.PostAsync("/api/commerce/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = Hidden(form, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static Task<HttpResponseMessage> SubmitCheckout(HttpClient client, string form) =>
        client.PostAsync("/api/commerce/checkout/place", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["quoteToken"] = Hidden(form, "quoteToken"), ["__RequestVerificationToken"] = Hidden(form, "__RequestVerificationToken")
        }));

    private static string Hidden(string form, string name)
    {
        var match = Regex.Match(form, $"name=\"{name}\" value=\"([^\"]+)\"");
        Assert.True(match.Success, $"Missing form field {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task WaitUntil(Func<Task<bool>> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await check()) await Task.Delay(200, timeout.Token);
    }

    [SqlFact]
    public async Task TwoSellerCart_CreatesOneOrderTwoSellerOrdersAndOneAppliedPayment()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            // Cart contains Seller 1 (offer1 on sku1: 200k x 1) AND Seller 2 (offer2a on SAME sku1: 180k x 1, offer2b on sku2: 100k x 2)
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 2, add: false);

            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, ""));

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");
            Assert.Equal(2, preview.Sellers.Count);
            Assert.Equal(580_000m, preview.Subtotal);
            Assert.Equal(50_000m, preview.Discount);
            Assert.Equal(60_000m, preview.Shipping);
            Assert.Equal(590_000m, preview.Total);

            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);

            await using var verify = factory.CreateDbContext();
            var order = await verify.Set<Order>()
                .Include(o => o.SellerOrders)
                .Include(o => o.Items)
                .Include(o => o.Payment)
                .Include(o => o.PaymentAttempts)
                .SingleAsync(o => o.Id == orderId);

            Assert.Null(order.SellerId);
            Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
            Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
            Assert.Equal(60_000m, order.ShippingVnd);
            Assert.Equal(590_000m, order.TotalVnd);
            Assert.Equal(2, order.SellerOrders.Count);
            Assert.Equal(3, order.Items.Count);

            var so1 = order.SellerOrders.Single(s => s.SellerId == Seller.FirstPartyId);
            var so2 = order.SellerOrders.Single(s => s.SellerId == fixture.Seller2Id);
            Assert.Equal(200_000m, so1.SubtotalVnd);
            Assert.Equal(0m, so1.CommissionVnd);
            Assert.Equal(30_000m, so1.ShippingVnd);

            Assert.Equal(380_000m, so2.SubtotalVnd);
            Assert.Equal(38_000m, so2.CommissionVnd);
            Assert.Equal(30_000m, so2.ShippingVnd);

            var attempt = Assert.Single(order.PaymentAttempts);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal(order.Number, attempt.ProviderInvoice);
            Assert.False(attempt.AppliedToOrder);

            var reservations = await verify.Set<StockReservation>().Where(r => r.OrderId == orderId).ToListAsync();
            Assert.Equal(3, reservations.Count);
            Assert.All(reservations, r =>
            {
                Assert.Equal(ReservationStatus.Held, r.Status);
                Assert.NotNull(r.OrderItemId);
                Assert.True(r.InventoryBalanceId > 0);
            });

            var sb1 = await verify.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id);
            Assert.Equal(1, sb1.Reserved);
            Assert.Equal(1, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).Reserved);
            Assert.Equal(1, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2AId)).Reserved);
            Assert.Equal(2, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2BId)).Reserved);

            var claim = await verify.Set<PromotionClaim>().SingleAsync(c => c.OrderId == orderId);
            Assert.Equal(ClaimStatus.Held, claim.Status);

            var placedEvent = await verify.Set<IntegrationOutboxEvent>().SingleAsync(e => e.EventType == "OrderPlaced");
            Assert.DoesNotContain(fixture.Buyer.Email, placedEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Nguyen Buyer", placedEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0901234567", placedEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("123 Le Loi", placedEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);

            var applied = await fixture.Payments.Apply(new GatewayOutcome(order.Number, "SEPAY-TX-100", preview.Total, "VND", true));
            Assert.True(applied);

            await using var afterPay = factory.CreateDbContext();
            var paidOrder = await afterPay.Set<Order>()
                .Include(o => o.SellerOrders)
                .Include(o => o.Payment)
                .Include(o => o.PaymentAttempts)
                .SingleAsync(o => o.Id == orderId);

            Assert.Equal(OrderStatus.Confirmed, paidOrder.Status);
            Assert.Equal(PaymentStatus.Paid, paidOrder.PaymentStatus);
            Assert.Equal(PaymentStatus.Paid, paidOrder.Payment.Status);
            Assert.All(paidOrder.SellerOrders, so => Assert.Equal(OrderStatus.Confirmed, so.Status));

            var paidAttempt = Assert.Single(paidOrder.PaymentAttempts);
            Assert.True(paidAttempt.AppliedToOrder);
            Assert.Equal(PaymentStatus.Paid, paidAttempt.Status);
            Assert.Equal(preview.Total, paidAttempt.CapturedAmountVnd);
            Assert.Equal("SEPAY-TX-100", paidAttempt.ProviderTransaction);

            Assert.Equal(1, await afterPay.Set<PaymentEvent>().CountAsync(e => e.OrderId == orderId));
            Assert.Equal(ClaimStatus.Redeemed, (await afterPay.Set<PromotionClaim>().SingleAsync(c => c.OrderId == orderId)).Status);
            Assert.Equal(1, await afterPay.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "PaymentCaptured"));
            Assert.Equal(2, await afterPay.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "SellerOrderConfirmed"));

            var day = DateOnly.FromDateTime(ShopSettings.VietnamTime(fixture.Clock.GetUtcNow()).DateTime);
            var reporting = new ReportingService(fixture.Store);
            var s1Sales = await reporting.SellerSales(fixture.Admin, Seller.FirstPartyId, day, day);
            var s2Sales = await reporting.SellerSales(fixture.SellerOwner, fixture.Seller2Id, day, day);
            Assert.Equal(so1.TotalVnd, s1Sales.Collected);
            Assert.Equal(so2.TotalVnd, s2Sales.Collected);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task FailedStockReservation_RollsBackEntireCheckoutTransaction()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 2, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");

            // Drop Seller 2 stock concurrently after quote was prepared.
            await using (var concurrent = factory.CreateDbContext())
            {
                var bal2b = await concurrent.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2BId);
                bal2b.OnHand = 1;
                await concurrent.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<ShopRuleException>(() => fixture.Checkout.Place(fixture.Buyer, preview.Quote));

            await using var verify = factory.CreateDbContext();
            Assert.Equal(0, await verify.Set<Order>().CountAsync());
            Assert.Equal(0, await verify.Set<SellerOrder>().CountAsync());
            Assert.Equal(0, await verify.Set<OrderItem>().CountAsync());
            Assert.Equal(0, await verify.Set<StockReservation>().CountAsync());
            Assert.Equal(0, await verify.Set<PaymentAttempt>().CountAsync());
            Assert.Equal(0, await verify.Set<PromotionClaim>().CountAsync());
            Assert.Equal(0, await verify.Set<IntegrationOutboxEvent>().CountAsync());
            Assert.Equal(0, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).Reserved);
            Assert.Equal(0, (await verify.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id)).Reserved);
            Assert.Equal(2, await verify.Set<HKShop.Domain.Cart.CartItem>().CountAsync());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task RepeatedPlaceWithSameSubmissionKey_ReturnsSameOrderWithoutDoubleReservation()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 2, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 1, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var firstOrderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);
            var secondOrderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);

            Assert.Equal(firstOrderId, secondOrderId);

            await using var verify = factory.CreateDbContext();
            Assert.Equal(1, await verify.Set<Order>().CountAsync());
            Assert.Equal(2, await verify.Set<SellerOrder>().CountAsync());
            Assert.Equal(2, await verify.Set<StockReservation>().CountAsync());
            Assert.Equal(2, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).Reserved);
            Assert.Equal(1, (await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2BId)).Reserved);
            Assert.Equal(1, await verify.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "OrderPlaced"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task DuplicatePaymentEvent_HasOneBusinessEffect()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var gateway = new StubGateway();
            var fixture = await SeedMarketplaceAsync(factory, context, gateway);

            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);
            var order = await context.Set<Order>().AsNoTracking().SingleAsync(o => o.Id == orderId);

            var outcome = new GatewayOutcome(order.Number, "TX-IDEMPOTENT-1", preview.Total, "VND", true);
            gateway.NextOutcome = outcome;

            Assert.True(await fixture.Payments.Apply(outcome));
            Assert.True(await fixture.Payments.Apply(outcome));
            await fixture.Payments.Reconcile(orderId, order.Number, CancellationToken.None);

            await using var verify = factory.CreateDbContext();
            Assert.Equal(1, await verify.Set<PaymentEvent>().CountAsync(e => e.OrderId == orderId));
            Assert.Equal(0, await verify.Set<Refund>().CountAsync(r => r.OrderId == orderId));
            Assert.Equal(1, await verify.Set<PaymentAttempt>().CountAsync(a => a.OrderId == orderId && a.AppliedToOrder));
            Assert.Equal(1, await verify.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "PaymentCaptured"));
            Assert.Equal(2, await verify.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "SellerOrderConfirmed"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task LateAndExcessCaptures_RecordAttemptsEventsAndRefundsWithoutReopeningExpiredOrder()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            // Part A: Late capture after expiration
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, add: false);
            var expiredPreview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var expiredOrderId = await fixture.Checkout.Place(fixture.Buyer, expiredPreview.Quote);
            var expiredNumber = await context.Set<Order>().Where(o => o.Id == expiredOrderId).Select(o => o.Number).SingleAsync();

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(16);
            var expiredCount = await fixture.Payments.Expire();
            Assert.Equal(1, expiredCount);

            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(expiredNumber, "TX-LATE-CAPTURE", expiredPreview.Total, "VND", true)));

            await using (var verifyLate = factory.CreateDbContext())
            {
                var order = await verifyLate.Set<Order>()
                    .Include(o => o.SellerOrders)
                    .Include(o => o.PaymentAttempts)
                    .SingleAsync(o => o.Id == expiredOrderId);

                Assert.Equal(OrderStatus.Expired, order.Status);
                Assert.Equal(PaymentStatus.Expired, order.PaymentStatus);
                Assert.All(order.SellerOrders, so => Assert.Equal(OrderStatus.Expired, so.Status));
                Assert.All(await verifyLate.Set<StockReservation>().Where(r => r.OrderId == expiredOrderId).ToListAsync(),
                    r => Assert.Equal(ReservationStatus.Released, r.Status));
                Assert.Equal(0, (await verifyLate.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).Reserved);
                Assert.Equal(0, (await verifyLate.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2AId)).Reserved);

                var lateAttempt = Assert.Single(order.PaymentAttempts);
                Assert.Equal(PaymentStatus.Paid, lateAttempt.Status);
                Assert.False(lateAttempt.AppliedToOrder);
                Assert.Equal(expiredPreview.Total, lateAttempt.CapturedAmountVnd);

                var lateEvent = await verifyLate.Set<PaymentEvent>().SingleAsync(e => e.OrderId == expiredOrderId);
                Assert.Equal("LateCapture", lateEvent.EventType);

                var lateRefund = await verifyLate.Set<Refund>().SingleAsync(r => r.OrderId == expiredOrderId);
                Assert.Equal(RefundStatus.Pending, lateRefund.Status);
                Assert.Equal(expiredPreview.Total, lateRefund.AmountVnd);
                Assert.Equal(lateAttempt.Id, lateRefund.PaymentAttemptId);

                var issues = await fixture.Payments.Issues(fixture.Admin);
                Assert.Contains(issues, i => i.OrderId == expiredOrderId && i.CapturedVnd == expiredPreview.Total);
            }

            // BuyAgain restores both Seller 1 & Seller 2 items from expired marketplace order
            var adjusted = await fixture.Cart.BuyAgain(fixture.Buyer, expiredOrderId);
            Assert.Equal(0, adjusted);
            var restoredCart = await fixture.Cart.Get(fixture.Buyer.UserId, null);
            Assert.Equal(2, restoredCart.Lines.Count);

            // Part B: Retry attempt + duplicate/excess captures
            var paidPreview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var paidOrderId = await fixture.Checkout.Place(fixture.Buyer, paidPreview.Quote);
            var firstInvoice = await context.Set<Order>().Where(o => o.Id == paidOrderId).Select(o => o.Number).SingleAsync();
            var retryInvoice = await fixture.Payments.CreateRetryAttempt(fixture.Buyer, paidOrderId);

            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(firstInvoice, "TX-PRIMARY-1", paidPreview.Total, "VND", true)));
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(retryInvoice, "TX-EXCESS-2", paidPreview.Total, "VND", true)));
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(firstInvoice, "TX-EXCESS-3", 50_000m, "VND", true)));

            await using var verifyExcess = factory.CreateDbContext();
            var attempts = await verifyExcess.Set<PaymentAttempt>()
                .Where(a => a.OrderId == paidOrderId)
                .OrderBy(a => a.AttemptNumber)
                .ToListAsync();

            Assert.Equal(3, attempts.Count);
            Assert.True(attempts[0].AppliedToOrder);
            Assert.False(attempts[1].AppliedToOrder);
            Assert.False(attempts[2].AppliedToOrder);
            Assert.Null(attempts[2].ProviderInvoice);
            Assert.All(attempts, a => Assert.Equal(PaymentStatus.Paid, a.Status));

            var events = await verifyExcess.Set<PaymentEvent>().Where(e => e.OrderId == paidOrderId).ToListAsync();
            Assert.Equal(3, events.Count);
            Assert.Equal(2, events.Count(e => e.EventType == "ExcessCapture"));

            var excessRefunds = await verifyExcess.Set<Refund>()
                .Where(r => r.OrderId == paidOrderId && r.Status == RefundStatus.Pending)
                .ToListAsync();
            Assert.Equal(2, excessRefunds.Count);
            Assert.Equal(paidPreview.Total + 50_000m, excessRefunds.Sum(r => r.AmountVnd));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task SingleSellerFirstPartyCodOrder_SucceedsAndKeepsInventoryInSync()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 2, add: false);
            var cartView = await fixture.Cart.Get(fixture.Buyer.UserId, null);
            Assert.True(cartView.SupportsCod);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, "");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);

            var orderService = new OrderService(fixture.Store, fixture.Clock);
            await orderService.StartPacking(fixture.Staff, orderId);
            await orderService.Dispatch(fixture.Staff, orderId, "GHN", "GHN-123");
            await orderService.Deliver(fixture.Staff, orderId, preview.Total, "COD-123");

            await using var verify = factory.CreateDbContext();
            var order = await verify.Set<Order>()
                .Include(o => o.SellerOrders)
                .Include(o => o.Payment)
                .Include(o => o.PaymentAttempts)
                .SingleAsync(o => o.Id == orderId);

            Assert.Equal(OrderStatus.Delivered, order.Status);
            Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
            Assert.Equal(PaymentStatus.Paid, order.Payment.Status);
            Assert.Equal(OrderStatus.Delivered, Assert.Single(order.SellerOrders).Status);
            Assert.True(Assert.Single(order.PaymentAttempts).AppliedToOrder);
            Assert.Equal(ReservationStatus.Consumed, (await verify.Set<StockReservation>().SingleAsync(r => r.OrderId == orderId)).Status);

            var stock = await verify.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id);
            var inv = await verify.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id);
            Assert.Equal((3, 0), (stock.OnHand, stock.Reserved));
            Assert.Equal((3, 0), (inv.OnHand, inv.Reserved));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task Phase2ToPhase3Migration_BackfillsExistingOrdersAndDropsSyncTriggers()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261113040000_MarketplaceOffersAndInventory");

            var buyer = await CreateUserAsync(context, "phase2-buyer");
            var category = new Category { Name = "Electronics", Active = true };
            context.Add(category);
            await context.SaveChangesAsync();

            var product = new Product
            {
                CategoryId = category.Id, Name = "Phone Case", Slug = "phone-case", Description = "Desc",
                Images = [new ProductImage { FileName = "case.webp", Alt = "Case" }]
            };
            context.Add(product);
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.Products SET Status = 1 WHERE Id = {product.Id};
                INSERT dbo.Skus (ProductId, Code, Size, Color, PriceVnd, Active)
                VALUES ({product.Id}, N'SKU-P2', N'M', N'Black', 120000, 1);
                DECLARE @skuId bigint = SCOPE_IDENTITY();
                INSERT dbo.StockBalances (SkuId, OnHand, Reserved, LowThreshold)
                VALUES (@skuId, 5, 1, 5);
                """);
            var sku = await context.Set<Sku>().SingleAsync(s => s.Code == "SKU-P2");

            var orderId = Guid.NewGuid();
            var number = $"HK{orderId:N}".ToUpperInvariant();
            var now = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.Orders (Id, SellerId, Number, CustomerId, CustomerEmail, SubmissionKey, CreatedAt, Status, Recipient, Phone, ShippingAddress, ShippingVnd, TotalVnd)
                VALUES ({orderId}, 1, {number}, {buyer.UserId}, {buyer.Email}, {Guid.NewGuid()}, {now}, 1, N'Receiver', N'0901', N'Address', 30000, 150000);

                INSERT dbo.OrderItems (OrderId, SkuId, SkuCode, Name, Quantity, UnitPriceVnd, DiscountVnd)
                VALUES ({orderId}, {sku.Id}, {sku.Code}, N'Phone Case', 1, 120000, 0);

                INSERT dbo.StockReservations (OrderId, SkuId, Quantity, Status)
                VALUES ({orderId}, {sku.Id}, 1, 0);

                INSERT dbo.Payments (OrderId, Method, Status, AmountVnd, PaidAt, ProviderTransaction, CollectionReference, Issue)
                VALUES ({orderId}, 1, 2, 150000, {now}, N'TX-PHASE2', N'', N'');

                INSERT dbo.PaymentEvents (OrderId, OutcomeKey, RecordedAt)
                VALUES ({orderId}, N'SePay:TX-PHASE2:paid', {now});

                INSERT dbo.Refunds (OrderId, BusinessKey, AmountVnd, Status, Reference, RequestedAt)
                VALUES ({orderId}, N'REF-PHASE2', 50000, 0, N'', {now});
                """);

            await context.Database.MigrateAsync();

            await using var verify = factory.CreateDbContext();
            var sellerOrder = await verify.Set<SellerOrder>().SingleAsync(so => so.OrderId == orderId);
            Assert.Equal((Seller.FirstPartyId, 120_000m, 30_000m, 150_000m, 0m),
                (sellerOrder.SellerId, sellerOrder.SubtotalVnd, sellerOrder.ShippingVnd, sellerOrder.TotalVnd, sellerOrder.CommissionVnd));

            var order = await verify.Set<Order>().SingleAsync(o => o.Id == orderId);
            Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);

            var orderItem = await verify.Set<OrderItem>().SingleAsync(oi => oi.OrderId == orderId);
            var expectedOffer = await verify.Set<SellerOffer>().SingleAsync(so => so.SellerId == Seller.FirstPartyId && so.SkuId == sku.Id);
            Assert.Equal(sellerOrder.Id, orderItem.SellerOrderId);
            Assert.Equal(expectedOffer.Id, orderItem.OfferId);
            Assert.Equal("Phone Case / M / Black", orderItem.ProductAndVariantSnapshot);

            var reservation = await verify.Set<StockReservation>().SingleAsync(sr => sr.OrderId == orderId);
            Assert.Equal(orderItem.Id, reservation.OrderItemId);
            Assert.Equal(expectedOffer.Id, reservation.OfferId);
            Assert.True(reservation.InventoryBalanceId > 0);

            var attempt = await verify.Set<PaymentAttempt>().SingleAsync(pa => pa.OrderId == orderId);
            Assert.True(attempt.AppliedToOrder);
            Assert.Equal(number, attempt.ProviderInvoice);
            Assert.Equal(150_000m, attempt.CapturedAmountVnd);

            var paymentEvent = await verify.Set<PaymentEvent>().SingleAsync(pe => pe.OrderId == orderId);
            Assert.Equal(attempt.Id, paymentEvent.PaymentAttemptId);
            Assert.Equal("SePay:TX-PHASE2:paid", paymentEvent.EventKey);

            var refund = await verify.Set<Refund>().SingleAsync(r => r.OrderId == orderId);
            Assert.Equal(attempt.Id, refund.PaymentAttemptId);
            Assert.Equal(sellerOrder.Id, refund.SellerOrderId);

            // Verify Phase 2 sync triggers were dropped cleanly while TR_Skus_FirstPartyOffer remains.
            var remainingTriggerCount = await verify.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM sys.triggers WHERE name IN (N'TR_StockBalances_FirstPartyInventory', N'TR_StockMovements_FirstPartyHistory')")
                .SingleAsync();
            Assert.Equal(0, remainingTriggerCount);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private sealed record MarketplaceFixture(
        IShopStore Store,
        TestClock Clock,
        ShopActor Admin,
        ShopActor Staff,
        ShopActor Buyer,
        ShopActor SellerOwner,
        long AddressId,
        long Seller2Id,
        long Sku1Id,
        long Sku2Id,
        long Offer1Id,
        long Offer2AId,
        long Offer2BId,
        CartService Cart,
        CheckoutService Checkout,
        PaymentService Payments);

    private static async Task<MarketplaceFixture> SeedMarketplaceAsync(
        SqlTestDatabase factory,
        ShopDbContext context,
        StubGateway? gateway = null)
    {
        var clock = new TestClock();
        var store = new SqlShopStore(factory);
        var settings = new ShopSettings { FlatShippingVnd = 30_000m, DefaultCommissionBps = 1000, PublicOrigin = "https://shop.example.test" };

        var admin = await CreateUserAsync(context, "admin", admin: true, staff: true);
        var staff = await CreateUserAsync(context, "staff", staff: true);
        var buyer = await CreateUserAsync(context, "buyer");
        var sellerOwner = await CreateUserAsync(context, "seller-owner");

        var address = new CustomerAddress
        {
            CustomerId = buyer.UserId,
            Recipient = "Nguyen Buyer",
            Phone = "0901234567",
            AddressText = "123 Le Loi, District 1"
        };
        context.Add(address);

        var category = new Category { Name = "Apparel", Active = true };
        context.Add(category);
        await context.SaveChangesAsync();

        var sku1 = new Sku { Code = "SKU-1", Size = "L", Color = "Blue", Active = true, PriceVnd = 200_000m };
        var product1 = new Product
        {
            CategoryId = category.Id, Name = "Classic Tee", Slug = "classic-tee", Description = "Tee",
            Skus = [sku1], Images = [new ProductImage { FileName = "p1.webp", Alt = "P1" }]
        };
        product1.Publish(true);

        var sku2 = new Sku { Code = "SKU-2", Size = "M", Color = "Black", Active = true, PriceVnd = null };
        var product2 = new Product
        {
            CategoryId = category.Id, Name = "Seller Cap", Slug = "seller-cap", Description = "Cap",
            Skus = [sku2], Images = [new ProductImage { FileName = "p2.webp", Alt = "P2" }]
        };
        product2.Publish(true);
        context.AddRange(product1, product2);
        await context.SaveChangesAsync();

        var inventory = new InventoryService(store, clock);
        await inventory.Adjust(staff, sku1.Id, 5, 1, "Initial stock");

        var sellers = new SellerService(store, clock);
        var seller2Id = await sellers.Register(sellerOwner, "Partner Store", "partner-store");
        await sellers.SetStatus(admin, seller2Id, SellerStatus.Active, null);

        var loc2 = new SellerLocation { SellerId = seller2Id, Name = "Partner Warehouse", Active = true };
        context.Add(loc2);
        await context.SaveChangesAsync();

        var offer1 = await context.Set<SellerOffer>().SingleAsync(o => o.SellerId == Seller.FirstPartyId && o.SkuId == sku1.Id);
        var offer2a = new SellerOffer { SellerId = seller2Id, SkuId = sku1.Id, SellerSkuCode = "P2-SKU1", PriceVnd = 180_000m, Status = SellerOfferStatus.Approved };
        var offer2b = new SellerOffer { SellerId = seller2Id, SkuId = sku2.Id, SellerSkuCode = "P2-SKU2", PriceVnd = 100_000m, Status = SellerOfferStatus.Approved };
        context.AddRange(offer2a, offer2b);
        await context.SaveChangesAsync();

        context.AddRange(
            new InventoryBalance { SellerId = seller2Id, OfferId = offer2a.Id, LocationId = loc2.Id, OnHand = 5, Reserved = 0 },
            new InventoryBalance { SellerId = seller2Id, OfferId = offer2b.Id, LocationId = loc2.Id, OnHand = 5, Reserved = 0 });
        context.Add(new Promotion
        {
            Code = "SAVE50K", Active = true, IsPercentage = false, Value = 50_000m, MinimumVnd = 100_000m,
            UsageCap = 10, StartsAt = clock.GetUtcNow().AddDays(-1), EndsAt = clock.GetUtcNow().AddDays(1)
        });
        context.Add(new SellerMembership
        {
            SellerId = Seller.FirstPartyId,
            UserId = admin.UserId,
            Role = SellerRole.Owner,
            Status = SellerMembershipStatus.Active
        });
        await context.SaveChangesAsync();

        return new MarketplaceFixture(
            store, clock, admin, staff, buyer, sellerOwner, address.Id, seller2Id, sku1.Id, sku2.Id, offer1.Id, offer2a.Id, offer2b.Id,
            new CartService(store, clock),
            new CheckoutService(store, settings, clock),
            new PaymentService(store, gateway ?? new StubGateway(), clock));
    }

    private static async Task<ShopActor> CreateUserAsync(ShopDbContext context, string name, bool admin = false, bool staff = false)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var user = new ShopUser
        {
            Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, TwoFactorEnabled = true
        };
        context.Add(user);
        await context.SaveChangesAsync();
        return new ShopActor(user.Id, email, true, staff, admin) { Mfa = true };
    }

    private sealed class StubGateway : IPaymentGateway
    {
        public bool Available => true;
        public GatewayOutcome? NextOutcome { get; set; }
        public HostedCheckout BuildCheckout(string invoice, Guid order, decimal amount) => new("https://pay.example.test", []);
        public Task<GatewayOutcome?> Query(string invoice, CancellationToken cancellationToken) => Task.FromResult(NextOutcome);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
