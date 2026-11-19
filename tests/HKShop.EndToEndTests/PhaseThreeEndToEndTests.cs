using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Application.Transportation;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Infrastructure.Persistence;
using HKShop.Messaging;
using HKShop.Notification;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Payments.Api.Application;
using HKShop.Payments.Api.Infrastructure;
using HKShop.ServiceDefaults;
using HKShop.Shipping.Api.Infrastructure;
using HKShop.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Cat = HKShop.Catalog.Api.Domain;
using Id = HKShop.Identity.Api.Domain;
using Ord = HKShop.Ordering.Api.Domain;
using Pay = HKShop.Payments.Api.Domain;
using Ship = HKShop.Shipping.Api.Domain;

namespace HKShop.EndToEndTests;

public class KafkaSqlFactAttribute : FactAttribute
{
    public KafkaSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")))
            Skip = "Set HKSHOP_TEST_SQL and HKSHOP_TEST_KAFKA to run Phase 3 end-to-end tests.";
    }
}

public sealed class MailpitKafkaSqlFactAttribute : KafkaSqlFactAttribute
{
    public MailpitKafkaSqlFactAttribute()
    {
        if (Skip is null && Environment.GetEnvironmentVariable("HKSHOP_TEST_MAILPIT") != "1")
            Skip = "Set HKSHOP_TEST_MAILPIT=1 with local Mailpit on ports 1025/8025.";
    }
}

public sealed class PhaseThreeEndToEndTests
{
    private const string SigningKey = "hkshop-phase3-e2e-integration-signing-key-32bytes!";
    private const string IpnSecret = "sepay-e2e-ipn-secret";
    private const string TestPassword = "Phase3Password_123!";
    private static readonly JwtSettings Jwt = new()
    {
        Issuer = "https://hkshop.identity",
        Audience = "hkshop.services",
        SigningKey = SigningKey
    };

    // -----------------------------------------------------------------------
    // Step 3.1: Shipping — idempotent placement, duplicate & out-of-order events
    // -----------------------------------------------------------------------
    [KafkaSqlFact]
    public async Task Step31_Shipping_HandlesIdempotentPlacement_Duplicates_OutOfOrderPackingAndCancellation()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.ship.{Guid.NewGuid():N}.";
        var orderingTopic = prefix + "Ordering";
        var shippingTopic = prefix + "Shipping";
        var shipConn = NewDb("E2E_Ship");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-ship-");

        await CreateTopicsAsync(broker, orderingTopic, shippingTopic);
        try
        {
            await using var shippingApp = Host<ShippingDbContext>("Shipping", shipConn, temp.FullName, broker, shippingTopic, [orderingTopic]);
            _ = shippingApp.CreateClient(); // Start host & InboxConsumerWorker

            var now = DateTimeOffset.UtcNow;
            var orderId = Guid.NewGuid();
            var soNormal = Guid.NewGuid();
            var soPackFirst = Guid.NewGuid();
            var soCancelFirst = Guid.NewGuid();

            NewTransportParcel Parcel(Guid soId) => new(soId, 1, "1 Le Loi", new(10.77, 106.70), new(10.78, 106.71), 2100, "654321");

            // Case A: duplicate SellerOrderPlaced (same eventId AND fresh eventId) followed by SellerOrderPacked
            var placedA = new SellerOrderPlaced(soNormal, orderId, 1, "HK1-S1", OrderStatus.Confirmed, 250_000m, 30_000m,
                PaymentMethod.SePay, PaymentStatus.Paid, "Buyer A", "0901234567", "10 Nguyen Hue", Parcel(soNormal), now);
            var placedAEventId = Guid.NewGuid();
            await PublishAsync(broker, orderingTopic, soNormal.ToString("D"), placedA, placedAEventId);
            await PublishAsync(broker, orderingTopic, soNormal.ToString("D"), placedA, placedAEventId); // exact duplicate
            await PublishAsync(broker, orderingTopic, soNormal.ToString("D"), placedA, Guid.NewGuid()); // distinct redelivery
            var packedAtA = now.AddMinutes(5);
            await PublishAsync(broker, orderingTopic, soNormal.ToString("D"), new SellerOrderPacked(soNormal, orderId, 1, "HK1-S1", packedAtA));

            // Case B: SellerOrderPacked arrives BEFORE SellerOrderPlaced
            var packedAtB = now.AddMinutes(10);
            await PublishAsync(broker, orderingTopic, soPackFirst.ToString("D"), new SellerOrderPacked(soPackFirst, orderId, 1, "HK1-S2", packedAtB));
            await PublishAsync(broker, orderingTopic, soPackFirst.ToString("D"),
                new SellerOrderPlaced(soPackFirst, orderId, 1, "HK1-S2", OrderStatus.Confirmed, 180_000m, 25_000m,
                    PaymentMethod.SePay, PaymentStatus.Paid, "Buyer B", "0901234568", "20 Le Lai", Parcel(soPackFirst), now));

            // Case C: OrderCancelled arrives BEFORE SellerOrderPlaced, followed by late SellerOrderPacked and SellerOrderPlaced
            var cancelOrderId = Guid.NewGuid();
            await PublishAsync(broker, orderingTopic, cancelOrderId.ToString("D"),
                new OrderCancelled(cancelOrderId, "HK2", "c@example.test", "Cancelled early", [soCancelFirst], now));
            await PublishAsync(broker, orderingTopic, soCancelFirst.ToString("D"),
                new SellerOrderPacked(soCancelFirst, cancelOrderId, 1, "HK2-S1", now.AddMinutes(1)));
            await PublishAsync(broker, orderingTopic, soCancelFirst.ToString("D"),
                new SellerOrderPlaced(soCancelFirst, cancelOrderId, 1, "HK2-S1", OrderStatus.Confirmed, 120_000m, 20_000m,
                    PaymentMethod.SePay, PaymentStatus.Paid, "Buyer C", "0901234569", "30 Hai Ba Trung", Parcel(soCancelFirst), now));

            await using var db = new ShippingDbContext(new DbContextOptionsBuilder<ShippingDbContext>().UseSqlServer(shipConn).Options);
            await WaitUntilAsync(async () =>
                await db.TransportParcels.AsNoTracking().CountAsync() == 3 &&
                await db.TransportParcels.AsNoTracking().AnyAsync(p => p.SellerOrderId == soNormal && p.Stage == TransportStage.Packed),
                TimeSpan.FromSeconds(45));

            var parcelA = await db.TransportParcels.AsNoTracking().SingleAsync(p => p.SellerOrderId == soNormal);
            Assert.Equal(TransportStage.Packed, parcelA.Stage);
            Assert.Equal(packedAtA.AddHours(48), parcelA.PickupDueAt);

            var parcelB = await db.TransportParcels.AsNoTracking().SingleAsync(p => p.SellerOrderId == soPackFirst);
            var snapB = await db.OrderSnapshots.AsNoTracking().SingleAsync(s => s.SellerOrderId == soPackFirst);
            Assert.Equal(TransportStage.Packed, parcelB.Stage);
            Assert.Equal(packedAtB.AddHours(48), parcelB.PickupDueAt);
            Assert.Equal(OrderStatus.Packing, snapB.SellerOrderStatus);

            var parcelC = await db.TransportParcels.AsNoTracking().SingleAsync(p => p.SellerOrderId == soCancelFirst);
            var snapC = await db.OrderSnapshots.AsNoTracking().SingleAsync(s => s.SellerOrderId == soCancelFirst);
            Assert.Equal(TransportStage.Cancelled, parcelC.Stage);
            Assert.Equal("", parcelC.DeliveryCode);
            Assert.True(snapC.CancelledBeforePlacement);
            Assert.Equal(OrderStatus.Cancelled, snapC.SellerOrderStatus);
        }
        finally
        {
            await DeleteTopicsAsync(broker, orderingTopic, shippingTopic);
            await DropDatabasesAsync(shipConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Step 3.2: Catalog — private verification docs, seller events, atomic stock
    // -----------------------------------------------------------------------
    [KafkaSqlFact]
    public async Task Step32_Catalog_ProtectsVerificationDocs_PropagatesSellerEvents_AndReservesAllOrNothing()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.cat.{Guid.NewGuid():N}.";
        var catTopic = prefix + "Catalog";
        var ordTopic = prefix + "Ordering";
        var payTopic = prefix + "Payments";
        var shipTopic = prefix + "Shipping";
        var catConn = NewDb("E2E_Cat");
        var ordConn = NewDb("E2E_CatOrd");
        var payConn = NewDb("E2E_CatPay");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-cat-");

        await CreateTopicsAsync(broker, catTopic, ordTopic, payTopic, shipTopic);
        try
        {
            await using var catalogApp = Host<CatalogDbContext>("Catalog", catConn, temp.FullName, broker, catTopic, [ordTopic]);
            await using var orderingApp = Host<OrderingDbContext>("Ordering", ordConn, temp.FullName, broker, ordTopic, [catTopic, payTopic, shipTopic]);
            await using var paymentsApp = Host<PaymentsDbContext>("Payments", payConn, temp.FullName, broker, payTopic, [ordTopic, shipTopic, catTopic]);
            _ = orderingApp.CreateClient();
            _ = paymentsApp.CreateClient();

            // Seed pending store application with encrypted verification document + two offers (offer 1 has 5 units, offer 2 has 1 unit)
            var appId = Guid.NewGuid();
            var secretDoc = "PRIVATE-ID-DOCUMENT-BYTES"u8.ToArray();
            long offer1Id, offer2Id, sku1Id, sku2Id;
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                var protector = catalogApp.Services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("HKShop.StoreApplicationVerification.v1");
                catDb.StoreApplications.Add(new Cat.StoreApplication
                {
                    Id = appId,
                    UserId = "applicant-1",
                    Email = "applicant1@example.test",
                    StoreName = "Verified Artisans",
                    Status = StoreApplicationStatus.Pending,
                    SubmittedAt = DateTimeOffset.UtcNow
                });
                catDb.StoreApplicationVerifications.Add(new Cat.StoreApplicationVerification
                {
                    ApplicationId = appId,
                    NationalId = "012345678901",
                    BusinessLicenseNumber = "BL-2026-001"
                });
                catDb.StoreVerificationDocuments.Add(new Cat.StoreVerificationDocument
                {
                    ApplicationId = appId,
                    Kind = StoreDocumentKind.NationalIdFront,
                    ContentType = "image/png",
                    Data = protector.Protect(secretDoc)
                });
                (offer1Id, sku1Id, offer2Id, sku2Id) = await SeedTwoCatalogOffersAsync(catDb, stock1: 5, stock2: 1, secondSeller: false);
                await catDb.SaveChangesAsync();
            }

            // Policy check: customer & non-MFA admin cannot read private document; Admin+MFA can
            var customerJwt = ShopActorFactory.CreateJwtToken(new ShopActor("applicant-1", "applicant1@example.test", true, false, false), Jwt);
            var adminNoMfaJwt = ShopActorFactory.CreateJwtToken(new ShopActor("admin-nomfa", "a@example.test", true, false, false) { HasEmployeeRole = true }, Jwt);
            var adminJwt = ShopActorFactory.CreateJwtToken(new ShopActor("admin-1", "admin@example.test", true, Staff: true, Admin: true) { Mfa = true, HasEmployeeRole = true }, Jwt);

            using var catCustomerHttp = catalogApp.CreateClient();
            catCustomerHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customerJwt);
            Assert.Equal(HttpStatusCode.Forbidden, (await catCustomerHttp.GetAsync($"/internal/catalog/store-applications/{appId:D}/documents/{StoreDocumentKind.NationalIdFront}")).StatusCode);

            using var catNoMfaHttp = catalogApp.CreateClient();
            catNoMfaHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminNoMfaJwt);
            Assert.Equal(HttpStatusCode.Forbidden, (await catNoMfaHttp.GetAsync($"/internal/catalog/store-applications/{appId:D}/documents/{StoreDocumentKind.NationalIdFront}")).StatusCode);

            using var catAdminHttp = catalogApp.CreateClient();
            catAdminHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            var catClient = new CatalogServiceClient(catAdminHttp);
            var doc = await catClient.GetVerificationDocumentAsync(appId, StoreDocumentKind.NationalIdFront);
            Assert.NotNull(doc);
            Assert.Equal(secretDoc, doc.Value.Data);

            // Approve store application -> SellerStatusChanged & SellerMembershipChanged propagate to Ordering & Payments
            await catClient.DecideStoreApplicationAsync(appId, new(true, null));
            await using var ordDb = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ordConn).Options);
            await using var payDb = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(payConn).Options);
            await WaitUntilAsync(async () =>
                await ordDb.SellerMemberships.AsNoTracking().AnyAsync(m => m.UserId == "applicant-1" && m.Status == SellerMembershipStatus.Active) &&
                await payDb.SellerProjections.AsNoTracking().AnyAsync(s => s.DisplayName == "Verified Artisans" && s.Status == SellerStatus.Active) &&
                await payDb.SellerMemberships.AsNoTracking().AnyAsync(m => m.UserId == "applicant-1" && m.Role == SellerRole.Owner),
                TimeSpan.FromSeconds(45));

            // Atomic all-or-nothing multi-line reservation: line 1 asks 2 (ok), line 2 asks 3 (only 1 available) -> 0 reserved on line 1
            var atomicOrderId = Guid.NewGuid();
            var res = await catClient.ReserveStockAsync(new(atomicOrderId,
            [
                new(offer1Id, sku1Id, 1, 2, 100_000m),
                new(offer2Id, sku2Id, 1, 3, 150_000m)
            ]));
            Assert.False(res.Reserved);
            await using var checkDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options);
            var bal1 = await checkDb.InventoryBalances.AsNoTracking().SingleAsync(b => b.OfferId == offer1Id);
            Assert.Equal(0, bal1.Reserved);
            Assert.False(await checkDb.StockHolds.AsNoTracking().AnyAsync(h => h.OrderId == atomicOrderId));
        }
        finally
        {
            await DeleteTopicsAsync(broker, catTopic, ordTopic, payTopic, shipTopic);
            await DropDatabasesAsync(catConn, ordConn, payConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Step 3.3 + Notification: SePay happy path, duplicate/excess IPN, expiry + late capture refund + Mailpit
    // -----------------------------------------------------------------------
    [MailpitKafkaSqlFact]
    public async Task Step33_PaymentsAndNotification_HappyPath_DuplicateExcessCallbacks_ExpiryAndLateCaptureRefund()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.pay.{Guid.NewGuid():N}.";
        var idTopic = prefix + "Identity";
        var catTopic = prefix + "Catalog";
        var ordTopic = prefix + "Ordering";
        var payTopic = prefix + "Payments";
        var shipTopic = prefix + "Shipping";
        var idConn = NewDb("E2E_PayId");
        var catConn = NewDb("E2E_PayCat");
        var ordConn = NewDb("E2E_PayOrd");
        var payConn = NewDb("E2E_PayPay");
        var notifConn = NewDb("E2E_PayNotif");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-pay-");
        Process? notifWorker = null;

        await CreateTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
        try
        {
            await using var identityApp = Host<IdentityServiceDbContext>("Identity", idConn, temp.FullName, broker, idTopic, []);
            await using var catalogApp = Host<CatalogDbContext>("Catalog", catConn, temp.FullName, broker, catTopic, [ordTopic]);
            await using var paymentsApp = Host<PaymentsDbContext>("Payments", payConn, temp.FullName, broker, payTopic, [ordTopic, shipTopic, catTopic]);
            await using var orderingApp = Host<OrderingDbContext>("Ordering", ordConn, temp.FullName, broker, ordTopic, [catTopic, payTopic, shipTopic], services =>
            {
                services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalogApp.Server.CreateHandler);
            });

            notifWorker = StartNotificationWorker(notifConn, broker, ordTopic, $"{idTopic},{catTopic},{ordTopic},{payTopic},{shipTopic}", prefix);

            var buyerEmail = $"pay-{Guid.NewGuid():N}@example.test";
            var (buyerId, addressId, buyerToken) = await SeedBuyerAsync(identityApp, idConn, buyerEmail);
            long offerId;
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                (offerId, _, _, _) = await SeedTwoCatalogOffersAsync(catDb, stock1: 5, stock2: 5, secondSeller: false);
                await catDb.SaveChangesAsync();
            }
            await using (var ordDbSeed = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ordConn).Options))
            {
                ordDbSeed.Promotions.Add(new Ord.Promotion
                {
                    Code = "SAVE10K", Active = true, IsPercentage = false, Value = 10_000m, MinimumVnd = 50_000m,
                    UsageCap = 1, StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1)
                });
                await ordDbSeed.SaveChangesAsync();
            }

            using var ordHttp = orderingApp.CreateClient();
            ordHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerToken);
            var ordClient = new OrderingServiceClient(ordHttp);

            using var payHttp = paymentsApp.CreateClient();
            payHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerToken);

            // --- Part A: Happy path + duplicate callback + excess capture + Mailpit confirmation ---
            await ordClient.ChangeCartAsync(new(null, offerId, null, 1, false));
            var preview1 = await ordClient.QuoteAsync(new(addressId, PaymentMethod.SePay, ""));
            var placed1 = await ordClient.PlaceOrderAsync(preview1.Quote);

            await using var payDb = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(payConn).Options);
            await using var ordDb = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ordConn).Options);
            await using var catDbCheck = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options);

            await WaitUntilAsync(async () => await payDb.PaymentAttempts.AsNoTracking().AnyAsync(a => a.OrderId == placed1.OrderId), TimeSpan.FromSeconds(45));
            var attempt1 = await payDb.PaymentAttempts.AsNoTracking().SingleAsync(a => a.OrderId == placed1.OrderId);

            // First SePay IPN -> confirms order
            Assert.Equal(HttpStatusCode.OK, (await SendSePayIpnAsync(payHttp, attempt1.ProviderInvoice!, "TX-HAPPY-1", attempt1.AmountVnd)).StatusCode);
            // Duplicate SePay IPN with same tx -> idempotent no-op
            Assert.Equal(HttpStatusCode.OK, (await SendSePayIpnAsync(payHttp, attempt1.ProviderInvoice!, "TX-HAPPY-1", attempt1.AmountVnd)).StatusCode);
            // Excess capture with distinct tx -> queued for refund, order stays Confirmed
            Assert.Equal(HttpStatusCode.OK, (await SendSePayIpnAsync(payHttp, attempt1.ProviderInvoice!, "TX-EXCESS-2", attempt1.AmountVnd)).StatusCode);

            await WaitUntilAsync(async () =>
                (await ordDb.Orders.AsNoTracking().SingleAsync(o => o.Id == placed1.OrderId)).Status == OrderStatus.Confirmed,
                TimeSpan.FromSeconds(45));
            Assert.Single(await payDb.Refunds.AsNoTracking().Where(r => r.OrderId == placed1.OrderId).ToListAsync());

            // Verify Notification delivered OrderConfirmed email to Mailpit
            await WaitUntilAsync(async () => await CountMailpitAsync($"to:{buyerEmail} subject:\"Order confirmed\"") >= 1, TimeSpan.FromSeconds(45));

            // --- Part B: Expiry releases stock + coupon, and late capture queues refund without confirming ---
            await ordClient.ChangeCartAsync(new(null, offerId, null, 2, false));
            var preview2 = await ordClient.QuoteAsync(new(addressId, PaymentMethod.SePay, "SAVE10K"));
            var placed2 = await ordClient.PlaceOrderAsync(preview2.Quote);

            await WaitUntilAsync(async () => await payDb.PaymentAttempts.AsNoTracking().AnyAsync(a => a.OrderId == placed2.OrderId), TimeSpan.FromSeconds(45));
            var attempt2 = await payDb.PaymentAttempts.AsNoTracking().SingleAsync(a => a.OrderId == placed2.OrderId);

            // Force deadline into the past and run expiry
            await payDb.OrderProjections.Where(o => o.OrderId == placed2.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentDeadline, DateTimeOffset.UtcNow.AddMinutes(-5)));
            using (var scope = paymentsApp.Services.CreateScope())
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PaymentsProcessor>().ExpireDuePaymentsAsync(default));

            // Wait for Ordering to mark Expired & release coupon, and Catalog to release stock back to 1 (from order 1 only)
            await WaitUntilAsync(async () =>
                (await ordDb.Orders.AsNoTracking().SingleAsync(o => o.Id == placed2.OrderId)).Status == OrderStatus.Expired &&
                (await ordDb.PromotionClaims.AsNoTracking().SingleAsync(c => c.OrderId == placed2.OrderId)).Status == ClaimStatus.Released &&
                (await catDbCheck.InventoryBalances.AsNoTracking().SingleAsync(b => b.OfferId == offerId)).Reserved == 1,
                TimeSpan.FromSeconds(45));

            // Late SePay IPN arrives AFTER expiry -> queues refund, does NOT confirm order
            Assert.Equal(HttpStatusCode.OK, (await SendSePayIpnAsync(payHttp, attempt2.ProviderInvoice!, "TX-LATE-3", attempt2.AmountVnd)).StatusCode);
            var lateRefund = await payDb.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == placed2.OrderId);
            Assert.Equal(attempt2.AmountVnd, lateRefund.AmountVnd);
            Assert.Equal(OrderStatus.Expired, (await ordDb.Orders.AsNoTracking().SingleAsync(o => o.Id == placed2.OrderId)).Status);

            // Simulate refund submission + confirmation (2 passes on SePayRefundGatewayService with SimulateRefunds=true)
            using (var scope = paymentsApp.Services.CreateScope())
            {
                var proc = scope.ServiceProvider.GetRequiredService<PaymentsProcessor>();
                await proc.ReconcileRefundAsync(lateRefund.Id, default);
                await payDb.Refunds.Where(r => r.Id == lateRefund.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
                await proc.ReconcileRefundAsync(lateRefund.Id, default);
            }
            var completedRefund = await payDb.Refunds.AsNoTracking().SingleAsync(r => r.Id == lateRefund.Id);
            Assert.Equal(RefundStatus.Completed, completedRefund.Status);
            Assert.True(completedRefund.Simulated);

            // Verify Notification delivered expiry + refund emails to Mailpit too
            await WaitUntilAsync(async () =>
                await CountMailpitAsync($"to:{buyerEmail} subject:\"Order payment expired\"") >= 1 &&
                await CountMailpitAsync($"to:{buyerEmail} subject:\"Simulated refund completed\"") >= 1,
                TimeSpan.FromSeconds(45));
        }
        finally
        {
            StopWorker(notifWorker);
            await DeleteTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
            await DropDatabasesAsync(idConn, catConn, ordConn, payConn, notifConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Step 3.4: Ordering — cart revision guard, coupon compensation on stock failure, single-seller cancel
    // -----------------------------------------------------------------------
    [KafkaSqlFact]
    public async Task Step34_Ordering_RejectsStaleCartRevision_CompensatesCouponOnStockFailure_AndCancelsSingleSeller()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.ord.{Guid.NewGuid():N}.";
        var idTopic = prefix + "Identity";
        var catTopic = prefix + "Catalog";
        var ordTopic = prefix + "Ordering";
        var payTopic = prefix + "Payments";
        var shipTopic = prefix + "Shipping";
        var idConn = NewDb("E2E_OrdId");
        var catConn = NewDb("E2E_OrdCat");
        var ordConn = NewDb("E2E_OrdOrd");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-ord-");

        await CreateTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
        try
        {
            await using var identityApp = Host<IdentityServiceDbContext>("Identity", idConn, temp.FullName, broker, idTopic, []);
            await using var catalogApp = Host<CatalogDbContext>("Catalog", catConn, temp.FullName, broker, catTopic, [ordTopic]);
            await using var orderingApp = Host<OrderingDbContext>("Ordering", ordConn, temp.FullName, broker, ordTopic, [catTopic, payTopic, shipTopic], services =>
            {
                services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalogApp.Server.CreateHandler);
            });

            var (_, addressId, buyerToken) = await SeedBuyerAsync(identityApp, idConn, $"ord-{Guid.NewGuid():N}@example.test");
            long offer1Id, offer2Id;
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                (offer1Id, _, offer2Id, _) = await SeedTwoCatalogOffersAsync(catDb, stock1: 5, stock2: 5, secondSeller: true);
                await catDb.SaveChangesAsync();
            }
            await using (var ordDbSeed = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ordConn).Options))
            {
                ordDbSeed.Promotions.AddRange(
                    new Ord.Promotion
                    {
                        Code = "ONCE20K_A", Active = true, IsPercentage = false, Value = 20_000m, MinimumVnd = 50_000m,
                        UsageCap = 1, StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1)
                    },
                    new Ord.Promotion
                    {
                        Code = "ONCE20K_B", Active = true, IsPercentage = false, Value = 20_000m, MinimumVnd = 50_000m,
                        UsageCap = 1, StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1)
                    });
                await ordDbSeed.SaveChangesAsync();
            }

            using var ordHttp = orderingApp.CreateClient();
            ordHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerToken);
            var ordClient = new OrderingServiceClient(ordHttp);

            // 1. Cart revision guard
            await ordClient.ChangeCartAsync(new(null, offer1Id, null, 1, false));
            var staleQuote = await ordClient.QuoteAsync(new(addressId, PaymentMethod.Cod, ""));
            await ordClient.ChangeCartAsync(new(null, offer1Id, null, 2, false));
            await Assert.ThrowsAsync<ShopRuleException>(() => ordClient.PlaceOrderAsync(staleQuote.Quote));

            // 2. Deterministic coupon compensation when stock reservation fails in Catalog:
            //    Two buyers race for the last 1 unit, each applying their own single-use coupon.
            //    Whichever loses MUST have its order cancelled, coupon claim released, and cart retained!
            var (_, address2Id, buyer2Token) = await SeedBuyerAsync(identityApp, idConn, $"ord2-{Guid.NewGuid():N}@example.test");
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                var bal1 = await catDb.InventoryBalances.SingleAsync(b => b.OfferId == offer1Id);
                bal1.OnHand = 1;
                await catDb.SaveChangesAsync();
            }
            await ordClient.ChangeCartAsync(new(null, offer1Id, null, 1, false));
            using var ordHttp2 = orderingApp.CreateClient();
            ordHttp2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyer2Token);
            var ordClient2 = new OrderingServiceClient(ordHttp2);
            await ordClient2.ChangeCartAsync(new(null, offer1Id, null, 1, false));

            var q1 = await ordClient.QuoteAsync(new(addressId, PaymentMethod.Cod, "ONCE20K_A"));
            var q2 = await ordClient2.QuoteAsync(new(address2Id, PaymentMethod.Cod, "ONCE20K_B"));
            var placedPair = await Task.WhenAll(ordClient.PlaceOrderAsync(q1.Quote), ordClient2.PlaceOrderAsync(q2.Quote));

            await using var ordDb = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ordConn).Options);
            await WaitUntilAsync(async () =>
                await ordDb.Orders.AsNoTracking().CountAsync(o => placedPair.Select(p => p.OrderId).Contains(o.Id) && o.Status == OrderStatus.Confirmed) == 1 &&
                await ordDb.Orders.AsNoTracking().CountAsync(o => placedPair.Select(p => p.OrderId).Contains(o.Id) && o.Status == OrderStatus.Cancelled) == 1,
                TimeSpan.FromSeconds(45));

            var orders = await ordDb.Orders.AsNoTracking().Where(o => placedPair.Select(p => p.OrderId).Contains(o.Id)).ToListAsync();
            var winner = orders.Single(o => o.Status == OrderStatus.Confirmed);
            var loser = orders.Single(o => o.Status == OrderStatus.Cancelled);
            Assert.Equal(ClaimStatus.Redeemed, (await ordDb.PromotionClaims.AsNoTracking().SingleAsync(c => c.OrderId == winner.Id)).Status);
            Assert.Equal(ClaimStatus.Released, (await ordDb.PromotionClaims.AsNoTracking().SingleAsync(c => c.OrderId == loser.Id)).Status);
            var loserClient = loser.Id == placedPair[0].OrderId ? ordClient : ordClient2;
            Assert.Single((await loserClient.CartAsync(null)).Lines);

            // 3. Two-seller order -> cancel ONLY Seller 1's seller order -> ONLY Seller 1's hold is released
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                var bal1 = await catDb.InventoryBalances.SingleAsync(b => b.OfferId == offer1Id);
                bal1.OnHand = 10;
                await catDb.SaveChangesAsync();
            }
            await ordClient.ChangeCartAsync(new(null, offer1Id, null, 1, false));
            await ordClient.ChangeCartAsync(new(null, offer2Id, null, 1, false));
            var twoSellerQuote = await ordClient.QuoteAsync(new(addressId, PaymentMethod.SePay, ""));
            var twoSellerPlaced = await ordClient.PlaceOrderAsync(twoSellerQuote.Quote);

            await using var catDbCheck = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options);
            await WaitUntilAsync(async () =>
                await catDbCheck.StockHolds.AsNoTracking().CountAsync(h => h.OrderId == twoSellerPlaced.OrderId && h.Status == ReservationStatus.Held) == 2,
                TimeSpan.FromSeconds(45));

            // Confirm the two-seller order via PaymentCaptured event, then cancel only Seller 1's seller order
            await PublishAsync(broker, payTopic, twoSellerPlaced.OrderId.ToString("D"),
                new PaymentCaptured(Guid.NewGuid(), twoSellerPlaced.OrderId, "HK-TWO", twoSellerQuote.Subtotal + twoSellerQuote.Shipping, "TX-TWO", DateTimeOffset.UtcNow));
            await WaitUntilAsync(async () =>
                (await ordDb.Orders.AsNoTracking().SingleAsync(o => o.Id == twoSellerPlaced.OrderId)).Status == OrderStatus.Confirmed,
                TimeSpan.FromSeconds(45));

            var details = await ordClient.GetOrderDetailsAsync(twoSellerPlaced.OrderId);
            var seller1Order = details!.SellerOrders.Single(s => s.SellerId == 1);
            var seller2Order = details.SellerOrders.Single(s => s.SellerId != 1);

            var staffJwt = ShopActorFactory.CreateJwtToken(new ShopActor("staff-1", "staff@example.test", true, Staff: true, Admin: false) { Mfa = true, HasEmployeeRole = true }, Jwt);
            using var ordStaffHttp = orderingApp.CreateClient();
            ordStaffHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", staffJwt);
            await new OrderingServiceClient(ordStaffHttp).CancelSellerOrderAsync(seller1Order.Id, "Out of stock at seller 1");

            await WaitUntilAsync(async () =>
                (await catDbCheck.StockHolds.AsNoTracking().SingleAsync(h => h.OrderId == twoSellerPlaced.OrderId && h.SellerOrderId == seller1Order.Id)).Status == ReservationStatus.Released,
                TimeSpan.FromSeconds(45));
            var hold2 = await catDbCheck.StockHolds.AsNoTracking().SingleAsync(h => h.OrderId == twoSellerPlaced.OrderId && h.SellerOrderId == seller2Order.Id);
            Assert.Equal(ReservationStatus.Held, hold2.Status);
        }
        finally
        {
            await DeleteTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
            await DropDatabasesAsync(idConn, catConn, ordConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Separate Cutover: Identity — role isolation, MFA, refresh rotation, revocation
    // -----------------------------------------------------------------------
    [KafkaSqlFact]
    public async Task IdentityCutover_EnforcesRoleIsolation_Mfa_RefreshRotation_AndRevocationToShipping()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.id.{Guid.NewGuid():N}.";
        var idTopic = prefix + "Identity";
        var shipTopic = prefix + "Shipping";
        var idConn = NewDb("E2E_Id");
        var shipConn = NewDb("E2E_IdShip");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-id-");

        await CreateTopicsAsync(broker, idTopic, shipTopic);
        try
        {
            await using var identityApp = Host<IdentityServiceDbContext>("Identity", idConn, temp.FullName, broker, idTopic, []);
            await using var shippingApp = Host<ShippingDbContext>("Shipping", shipConn, temp.FullName, broker, shipTopic, [idTopic]);
            _ = shippingApp.CreateClient();

            string staffId, shipperId, staffTotpKey;
            using (var scope = identityApp.Services.CreateScope())
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var role in new[] { "Admin", "StoreStaff", "Shipper" })
                    if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));

                var users = scope.ServiceProvider.GetRequiredService<UserManager<Id.ShopUser>>();
                var customer = new Id.ShopUser { UserName = "cust@example.test", Email = "cust@example.test", EmailConfirmed = true, DisplayName = "Cust" };
                Assert.True((await users.CreateAsync(customer, TestPassword)).Succeeded);

                var staffNoMfa = new Id.ShopUser { UserName = "staff-nomfa@example.test", Email = "staff-nomfa@example.test", EmailConfirmed = true, DisplayName = "Staff NoMfa" };
                Assert.True((await users.CreateAsync(staffNoMfa, TestPassword)).Succeeded);
                await users.AddToRoleAsync(staffNoMfa, "StoreStaff");

                var staff = new Id.ShopUser { UserName = "staff@example.test", Email = "staff@example.test", EmailConfirmed = true, DisplayName = "Staff" };
                Assert.True((await users.CreateAsync(staff, TestPassword)).Succeeded);
                await users.AddToRoleAsync(staff, "StoreStaff");
                await users.ResetAuthenticatorKeyAsync(staff);
                await users.SetTwoFactorEnabledAsync(staff, true);
                staffTotpKey = (await users.GetAuthenticatorKeyAsync(staff))!;
                staffId = staff.Id;

                var shipper = new Id.ShopUser { UserName = "shipper@example.test", Email = "shipper@example.test", EmailConfirmed = true, DisplayName = "Shipper" };
                Assert.True((await users.CreateAsync(shipper, TestPassword)).Succeeded);
                shipperId = shipper.Id;
            }

            using var idHttp = identityApp.CreateClient();
            var idClient = new IdentityServiceClient(idHttp);

            // Role isolation
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.IssueTokenAsync(new("cust@example.test", TestPassword, "eoffice")));
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.IssueTokenAsync(new("staff@example.test", TestPassword, "commerce")));

            // EOffice MFA enrollment required when 2FA is off
            var needEnroll = await idClient.IssueTokenAsync(new("staff-nomfa@example.test", TestPassword, "eoffice"));
            Assert.True(needEnroll.RequiresMfaEnrollment);
            Assert.Null(needEnroll.AccessToken);

            // EOffice MFA challenge + valid TOTP issuance
            var needCode = await idClient.IssueTokenAsync(new("staff@example.test", TestPassword, "eoffice"));
            Assert.True(needCode.RequiresTwoFactor);
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.IssueTokenAsync(new("staff@example.test", TestPassword, "eoffice", "000000")));
            var staffTokens = await idClient.IssueTokenAsync(new("staff@example.test", TestPassword, "eoffice", ComputeTotp(staffTotpKey)));
            Assert.False(string.IsNullOrWhiteSpace(staffTokens.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(staffTokens.RefreshToken));

            // Refresh token rotation invalidates old refresh token
            var rotated = await idClient.RefreshTokenAsync(staffTokens.RefreshToken!);
            Assert.NotEqual(staffTokens.RefreshToken, rotated.RefreshToken);
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.RefreshTokenAsync(staffTokens.RefreshToken!));

            // Admin enables shipper -> propagates UserRolesChanged to Shipping; Admin disables shipper -> revokes tokens & deactivates in Shipping
            var adminJwt = ShopActorFactory.CreateJwtToken(new ShopActor("admin-1", "admin@example.test", true, Staff: true, Admin: true) { Mfa = true, HasEmployeeRole = true }, Jwt);
            idHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            await idClient.SetShipperRoleAsync("shipper@example.test", true);

            await using var shipDb = new ShippingDbContext(new DbContextOptionsBuilder<ShippingDbContext>().UseSqlServer(shipConn).Options);
            await WaitUntilAsync(async () =>
                await shipDb.TransportShippers.AsNoTracking().AnyAsync(s => s.UserId == shipperId && s.Active),
                TimeSpan.FromSeconds(45));

            await idClient.SetUserDisabledAsync(staffId, true);
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.RefreshTokenAsync(rotated.RefreshToken!));

            await idClient.SetUserDisabledAsync(shipperId, true);
            await WaitUntilAsync(async () =>
                await shipDb.TransportShippers.AsNoTracking().AnyAsync(s => s.UserId == shipperId && !s.Active),
                TimeSpan.FromSeconds(45));
        }
        finally
        {
            await DeleteTopicsAsync(broker, idTopic, shipTopic);
            await DropDatabasesAsync(idConn, shipConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Full BFF HTTP Cutover Smoke Test: HKShop.Web -> Identity + Catalog + Ordering + Payments + Shipping
    // -----------------------------------------------------------------------
    [KafkaSqlFact]
    public async Task WebBff_CompletesLoginCartCheckoutAndOrderPageAgainstExtractedServicesWithoutMonolithWrites()
    {
        var broker = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var prefix = $"hkshop.e2e.bff.{Guid.NewGuid():N}.";
        var idTopic = prefix + "Identity";
        var catTopic = prefix + "Catalog";
        var ordTopic = prefix + "Ordering";
        var payTopic = prefix + "Payments";
        var shipTopic = prefix + "Shipping";
        var monoConn = NewDb("E2E_BffMono");
        var idConn = NewDb("E2E_BffId");
        var catConn = NewDb("E2E_BffCat");
        var ordConn = NewDb("E2E_BffOrd");
        var payConn = NewDb("E2E_BffPay");
        var shipConn = NewDb("E2E_BffShip");
        var temp = Directory.CreateTempSubdirectory("hkshop-e2e-bff-");

        await CreateTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
        try
        {
            // Prepare an empty monolith DB to prove Web BFF writes ZERO carts or orders to it!
            await using (var monoDb = new ShopDbContext(new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(monoConn).Options))
                await monoDb.Database.MigrateAsync();

            await using var identityApp = Host<IdentityServiceDbContext>("Identity", idConn, temp.FullName, broker, idTopic, []);
            await using var catalogApp = Host<CatalogDbContext>("Catalog", catConn, temp.FullName, broker, catTopic, [ordTopic]);
            await using var shippingApp = Host<ShippingDbContext>("Shipping", shipConn, temp.FullName, broker, shipTopic, [ordTopic, payTopic, catTopic, idTopic]);
            await using var paymentsApp = Host<PaymentsDbContext>("Payments", payConn, temp.FullName, broker, payTopic, [ordTopic, shipTopic, catTopic]);
            await using var orderingApp = Host<OrderingDbContext>("Ordering", ordConn, temp.FullName, broker, ordTopic, [catTopic, payTopic, shipTopic], services =>
            {
                services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalogApp.Server.CreateHandler);
                services.AddHttpClient<ShippingServiceClient>().ConfigurePrimaryHttpMessageHandler(shippingApp.Server.CreateHandler);
            });

            var buyerEmail = $"bff-{Guid.NewGuid():N}@example.test";
            var (_, addressId, _) = await SeedBuyerAsync(identityApp, idConn, buyerEmail);
            long offerId;
            await using (var catDb = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catConn).Options))
            {
                (offerId, _, _, _) = await SeedTwoCatalogOffersAsync(catDb, stock1: 5, stock2: 5, secondSeller: false);
                await catDb.SaveChangesAsync();
            }

            using var webApp = new WebApplicationFactory<ShopFailureFilter>().WithWebHostBuilder(web => web
                .UseSetting("ConnectionStrings:HKShop", monoConn)
                .UseSetting("Shop:KeysPath", Path.Combine(temp.FullName, "web-keys"))
                .UseSetting("Shop:MediaPath", Path.Combine(temp.FullName, "web-media"))
                .UseSetting("Shop:PublicOrigin", "https://localhost")
                .UseSetting("Jwt:Issuer", Jwt.Issuer)
                .UseSetting("Jwt:Audience", Jwt.Audience)
                .UseSetting("Jwt:SigningKey", Jwt.SigningKey)
                .UseSetting("Services:Identity:BaseUrl", "http://identity.test")
                .UseSetting("Services:Catalog:BaseUrl", "http://catalog.test")
                .UseSetting("Services:Ordering:BaseUrl", "http://ordering.test")
                .UseSetting("Services:Payments:BaseUrl", "http://payments.test")
                .UseSetting("Services:Shipping:BaseUrl", "http://shipping.test")
                .UseSetting("Identity:UseJwtAuth", "true")
                .UseSetting("Notification:RetireLegacyEmailWorker", "true")
                .UseSetting("HttpsRedirection:Enabled", "false")
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                    services.AddHttpClient("IdentityTokenRefresh").ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                    services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalogApp.Server.CreateHandler);
                    services.AddHttpClient<OrderingServiceClient>().ConfigurePrimaryHttpMessageHandler(orderingApp.Server.CreateHandler);
                    services.AddHttpClient<PaymentsServiceClient>().ConfigurePrimaryHttpMessageHandler(paymentsApp.Server.CreateHandler);
                    services.AddHttpClient<ShippingServiceClient>().ConfigurePrimaryHttpMessageHandler(shippingApp.Server.CreateHandler);
                }));

            using var browser = webApp.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

            // 1. Login via BFF -> calls IdentityServiceClient.IssueTokenAsync and stores JWT+refresh in cookie
            var loginHtml = await browser.GetStringAsync("/account/login");
            using var loginResp = await browser.PostAsync("/api/commerce/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = buyerEmail,
                ["password"] = TestPassword,
                ["__RequestVerificationToken"] = HiddenValue(loginHtml, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.Redirect, loginResp.StatusCode);

            // 2. Add to cart via BFF -> calls OrderingServiceClient.ChangeCartAsync
            var cartPageBefore = await browser.GetStringAsync("/cart");
            var loginForCsrf = await browser.GetStringAsync("/account/addresses");
            using var addResp = await browser.PostAsync("/api/commerce/cart", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["offerId"] = offerId.ToString(),
                ["quantity"] = "1",
                ["add"] = "true",
                ["__RequestVerificationToken"] = HiddenValue(loginForCsrf, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.Redirect, addResp.StatusCode);
            var cartHtml = await browser.GetStringAsync("/cart");
            Assert.Contains("Item One", cartHtml);

            // 3. Checkout quote & place via BFF -> calls OrderingServiceClient.QuoteAsync & PlaceOrderAsync
            var checkoutHtml = await browser.GetStringAsync($"/checkout?address={addressId}&method=SePay");
            Assert.Contains("Place Order", checkoutHtml);
            using var placeResp = await browser.PostAsync("/api/commerce/checkout/place", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["quoteToken"] = HiddenValue(checkoutHtml, "quoteToken"),
                ["__RequestVerificationToken"] = HiddenValue(checkoutHtml, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.SeeOther, placeResp.StatusCode);
            var orderPath = placeResp.Headers.Location!.OriginalString;
            Assert.StartsWith("/orders/", orderPath);
            var orderId = Guid.Parse(orderPath.Split('/')[^1]);

            // Wait for saga to create SePay PaymentAttempt in Payments
            await using var payDb = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(payConn).Options);
            await WaitUntilAsync(async () => await payDb.PaymentAttempts.AsNoTracking().AnyAsync(a => a.OrderId == orderId), TimeSpan.FromSeconds(45));

            // 4. Load /orders/{id} via BFF -> composes Ordering + Payments -> shows Pay with SePay form!
            var orderHtml = await browser.GetStringAsync(orderPath);
            Assert.Contains("Pay with SePay", orderHtml);

            // 5. Prove monolith DB received ZERO cart or order writes!
            await using var verifyMono = new ShopDbContext(new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(monoConn).Options);
            Assert.False(await verifyMono.Set<HKShop.Domain.Cart.ShoppingCart>().AnyAsync());
            Assert.False(await verifyMono.Set<HKShop.Domain.Orders.Order>().AnyAsync());
        }
        finally
        {
            await DeleteTopicsAsync(broker, idTopic, catTopic, ordTopic, payTopic, shipTopic);
            await DropDatabasesAsync(monoConn, idConn, catConn, ordConn, payConn, shipConn);
            temp.Delete(recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static async Task<(string UserId, long AddressId, string Token)> SeedBuyerAsync(
        WebApplicationFactory<IdentityServiceDbContext> identityApp, string idConn, string email)
    {
        string userId;
        long addressId;
        using (var scope = identityApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Id.ShopUser>>();
            var user = new Id.ShopUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "E2E Buyer" };
            Assert.True((await users.CreateAsync(user, TestPassword)).Succeeded);
            userId = user.Id;
        }
        await using (var db = new IdentityServiceDbContext(new DbContextOptionsBuilder<IdentityServiceDbContext>().UseSqlServer(idConn).Options))
        {
            var addr = new Id.CustomerAddress { CustomerId = userId, Recipient = "E2E Buyer", Phone = "0901234567", AddressText = "123 Le Loi" };
            db.CustomerAddresses.Add(addr);
            await db.SaveChangesAsync();
            addressId = addr.Id;
        }
        using var http = identityApp.CreateClient();
        var tokens = await new IdentityServiceClient(http).IssueTokenAsync(new(email, TestPassword, "commerce"));
        return (userId, addressId, tokens.AccessToken!);
    }

    private static async Task<(long Offer1Id, long Sku1Id, long Offer2Id, long Sku2Id)> SeedTwoCatalogOffersAsync(
        CatalogDbContext db, int stock1, int stock2, bool secondSeller)
    {
        var category = new Cat.Category { Name = $"Cat-{Guid.NewGuid():N}"[..20], Active = true };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        long seller2Id = 1;
        long loc2Id = 1;
        if (secondSeller)
        {
            var seller2 = new Cat.Seller { Slug = $"s2-{Guid.NewGuid():N}"[..16], DisplayName = "Partner Two", Status = SellerStatus.Active, CreatedAt = DateTimeOffset.UtcNow };
            db.Sellers.Add(seller2);
            await db.SaveChangesAsync();
            seller2Id = seller2.Id;
            var loc2 = new Cat.SellerLocation { SellerId = seller2Id, Name = "Partner Hub", PickupAddress = "99 Nguyen Trai", Active = true };
            db.SellerLocations.Add(loc2);
            await db.SaveChangesAsync();
            loc2Id = loc2.Id;
        }

        var p1 = new Cat.Product { SellerId = 1, CategoryId = category.Id, Name = "Item One", Slug = $"item-one-{Guid.NewGuid():N}"[..24], Description = "Desc", Status = ProductStatus.Published };
        var p2 = new Cat.Product { SellerId = seller2Id, CategoryId = category.Id, Name = "Item Two", Slug = $"item-two-{Guid.NewGuid():N}"[..24], Description = "Desc", Status = ProductStatus.Published };
        db.Products.AddRange(p1, p2);
        await db.SaveChangesAsync();

        var s1 = new Cat.Sku { ProductId = p1.Id, Code = $"SKU1-{Guid.NewGuid():N}"[..16], Size = "M", Color = "Blue", PriceVnd = 100_000m, Active = true, Stock = new Cat.StockBalance { OnHand = stock1, Reserved = 0, LowThreshold = 1 } };
        var s2 = new Cat.Sku { ProductId = p2.Id, Code = $"SKU2-{Guid.NewGuid():N}"[..16], Size = "L", Color = "Red", PriceVnd = 150_000m, Active = true, Stock = new Cat.StockBalance { OnHand = stock2, Reserved = 0, LowThreshold = 1 } };
        db.Skus.AddRange(s1, s2);
        await db.SaveChangesAsync();

        var o1 = new Cat.SellerOffer { SellerId = 1, SkuId = s1.Id, SellerSkuCode = s1.Code, PriceVnd = 100_000m, Status = SellerOfferStatus.Approved };
        var o2 = new Cat.SellerOffer { SellerId = seller2Id, SkuId = s2.Id, SellerSkuCode = s2.Code, PriceVnd = 150_000m, Status = SellerOfferStatus.Approved };
        db.SellerOffers.AddRange(o1, o2);
        await db.SaveChangesAsync();

        db.InventoryBalances.AddRange(
            new Cat.InventoryBalance { SellerId = 1, OfferId = o1.Id, LocationId = 1, OnHand = stock1, Reserved = 0 },
            new Cat.InventoryBalance { SellerId = seller2Id, OfferId = o2.Id, LocationId = loc2Id, OnHand = stock2, Reserved = 0 });
        await db.SaveChangesAsync();
        return (o1.Id, s1.Id, o2.Id, s2.Id);
    }

    private static Task<HttpResponseMessage> SendSePayIpnAsync(HttpClient client, string invoice, string txId, decimal amount)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments/sepay/ipn")
        {
            Content = JsonContent.Create(new
            {
                notification_type = "ORDER_PAID",
                order = new
                {
                    order_status = "CAPTURED",
                    order_invoice_number = invoice,
                    order_amount = amount.ToString("0"),
                    order_currency = "VND"
                },
                transaction = new
                {
                    id = txId,
                    transaction_status = "APPROVED",
                    transaction_type = "PAYMENT",
                    transaction_amount = amount.ToString("0"),
                    transaction_currency = "VND"
                }
            })
        };
        req.Headers.TryAddWithoutValidation("X-Secret-Key", IpnSecret);
        return client.SendAsync(req);
    }

    private static async Task PublishAsync<T>(string broker, string topic, string key, T payload, Guid? eventId = null)
    {
        var envelope = new EventEnvelope(
            eventId ?? Guid.NewGuid(),
            typeof(T).Name,
            1,
            typeof(T).Name,
            key,
            DateTimeOffset.UtcNow,
            key,
            null,
            JsonSerializer.SerializeToElement(payload));
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = broker, EnableIdempotence = true }).Build();
        var res = await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = JsonSerializer.Serialize(envelope) });
        Assert.Equal(PersistenceStatus.Persisted, res.Status);
    }

    private static WebApplicationFactory<T> Host<T>(
        string service, string connection, string rootDir, string broker, string producerTopic, string[] subscribedTopics,
        Action<IServiceCollection>? configure = null) where T : class
    {
        var app = new WebApplicationFactory<T>().WithWebHostBuilder(web => web
            .UseSetting($"ConnectionStrings:{service}", connection)
            .UseSetting("Jwt:Issuer", Jwt.Issuer)
            .UseSetting("Jwt:Audience", Jwt.Audience)
            .UseSetting("Jwt:SigningKey", Jwt.SigningKey)
            .UseSetting("Shop:KeysPath", Path.Combine(rootDir, "keys"))
            .UseSetting("Shop:MediaPath", Path.Combine(rootDir, "media"))
            .UseSetting("Shop:PublicOrigin", "https://shop.example.test")
            .UseSetting("SePay:MerchantId", "test")
            .UseSetting("SePay:MerchantSecret", "test")
            .UseSetting("SePay:IpnSecret", IpnSecret)
            .UseSetting("SePay:SimulateRefunds", "true")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(new OutboxRelayOptions(broker, producerTopic, service));
                services.AddSingleton(new InboxConsumerOptions(broker, producerTopic + ".grp", service.ToLowerInvariant(), subscribedTopics));
                configure?.Invoke(services);
            }));
        _ = app.Server;
        return app;
    }

    private static Process StartNotificationWorker(string conn, string broker, string orderingTopic, string eventTopics, string groupPrefix)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(NotificationDbContext).Assembly.Location);
        start.Environment["ConnectionStrings__Notification"] = conn;
        start.Environment["Kafka__BootstrapServers"] = broker;
        start.Environment["Kafka__OrderingTopic"] = orderingTopic;
        start.Environment["Kafka__NotificationGroupId"] = groupPrefix + "notif.confirm";
        start.Environment["Kafka__EventTopics"] = eventTopics;
        start.Environment["Kafka__NotificationEventsGroupId"] = groupPrefix + "notif.events";
        start.Environment["Smtp__Host"] = "127.0.0.1";
        start.Environment["Smtp__Port"] = "1025";
        return Process.Start(start)!;
    }

    private static void StopWorker(Process? worker)
    {
        if (worker is null) return;
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            worker.WaitForExit(5000);
        }
        finally { worker.Dispose(); }
    }

    private static async Task<int> CountMailpitAsync(string query)
    {
        using var http = new HttpClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString(query)}"));
        return doc.RootElement.GetProperty("messages").GetArrayLength();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!await predicate())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(150, cts.Token);
        }
    }

    private static string HiddenValue(string html, string name)
    {
        var match = Regex.Match(html, $@"name=""{Regex.Escape(name)}""[^>]*value=""([^""]+)""|value=""([^""]+)""[^>]*name=""{Regex.Escape(name)}""");
        Assert.True(match.Success, $"Hidden form input '{name}' was not found.");
        return WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
    }

    private static string ComputeTotp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = base32Key.Trim().Replace(" ", "").ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in cleaned)
        {
            var val = alphabet.IndexOf(ch);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static string NewDb(string prefix) =>
        new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL"))
        {
            InitialCatalog = $"HKShopTests_{prefix}_{Guid.NewGuid():N}"
        }.ConnectionString;

    private static async Task CreateTopicsAsync(string broker, params string[] topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = broker }).Build();
        await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 }));
    }

    private static async Task DeleteTopicsAsync(string broker, params string[] topics)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = broker }).Build();
            await admin.DeleteTopicsAsync(topics);
        }
        catch { }
    }

    private static async Task DropDatabasesAsync(params string[] connections)
    {
        foreach (var conn in connections)
        {
            try
            {
                await using var db = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(conn).Options);
                await db.Database.EnsureDeletedAsync();
            }
            catch { }
        }
    }
}
