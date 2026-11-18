using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Settlement;
using HKShop.Application.Transportation;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;
using HKShop.Commerce.Application.Ports;
using HKShop.EOffice.Shipping.Application;
using HKShop.Infrastructure.Kafka;
using HKShop.Infrastructure.Persistence;
using HKShop.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed partial class SqlMarketplaceFulfillmentTests
{
    [SqlFact]
    public async Task TransportCheckoutPage_PlacesProtectedQuoteAndShowsDelivery()
    {
        var factory = SqlTestDatabase.Create();
        var files = Directory.CreateTempSubdirectory("hkshop-transport-");
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            await PrepareTransport(fixture);
            var user = await context.Users.SingleAsync(u => u.Id == fixture.Buyer.UserId);
            const string password = "TransportTest_123!";
            user.PasswordHash = new PasswordHasher<ShopUser>().HashPassword(user, password);
            user.TwoFactorEnabled = false;
            await context.SaveChangesAsync();
            const string bankKey = "transport-test-key-not-production";
            using var app = new WebApplicationFactory<ShopFailureFilter>().WithWebHostBuilder(web => web
                .UseSetting("ConnectionStrings:HKShop", context.Database.GetConnectionString())
                .UseSetting("Shop:KeysPath", Path.Combine(files.FullName, "keys"))
                .UseSetting("Shop:MediaPath", Path.Combine(files.FullName, "media"))
                .UseSetting("Shop:PublicOrigin", "https://localhost")
                .UseSetting("Kafka:BootstrapServers", "127.0.0.1:1")
                .UseSetting("Kafka:CheckoutTopic", "transport-test")
                .UseSetting("Kafka:CheckoutGroupId", "transport-test")
                .UseSetting("Transport:Enabled", "true")
                .UseSetting("Transport:RoutingBaseUrl", "http://localhost:5000")
                .UseSetting("Transport:BankAccount", "123456")
                .UseSetting("Transport:BankName", "Test bank")
                .UseSetting("Transport:BankWebhookKey", bankKey)
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<ITransportRouting>(new FixtureRoutes());
                }));
            using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            var login = await client.GetStringAsync("/account/login");
            using var signIn = await client.PostAsync("/api/commerce/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = fixture.Buyer.Email, ["password"] = password,
                ["__RequestVerificationToken"] = TransportFormValue(login, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal("/", signIn.Headers.Location?.OriginalString);

            var preview = await client.GetStringAsync($"/checkout?address={fixture.AddressId}&method=TransportAtDelivery");
            Assert.Contains("Due at delivery", preview);
            using var result = await client.PostAsync("/api/commerce/checkout/place", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["quoteToken"] = TransportFormValue(preview, "quoteToken"),
                ["__RequestVerificationToken"] = TransportFormValue(preview, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.SeeOther, result.StatusCode);
            var requestPath = result.Headers.Location!.OriginalString;
            Assert.StartsWith("/checkout/requests/", requestPath);
            var requestId = Guid.Parse(requestPath.Split('/')[^1]);
            using (var scope = app.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<CheckoutRequests>().Process(requestId);
            var orderId = await context.Set<CheckoutRequest>().Where(r => r.Id == requestId).Select(r => r.OrderId).SingleAsync();
            Assert.NotNull(orderId);
            Assert.Equal(2, await context.Set<TransportParcel>().CountAsync());
            Assert.Contains("HKShop delivery", await client.GetStringAsync($"/orders/{orderId:D}"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
            files.Delete(recursive: true);
        }
    }

    [SqlFact]
    public async Task TransportBankWebhookAuthenticatesAndDeduplicates()
    {
        await using var context = SqlTestDatabase.Create().CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            using var app = PlatformHttpTestHost.Create(context.Database.GetConnectionString(), enableTransport: true);
            using var client = app.CreateBrowser();
            var bankEvent = new
            {
                id = 419L, transferType = "in", accountNumber = PlatformHttpTestHost.BankAccount,
                transferAmount = 10_000, content = "Unmatched"
            };
            using var unauthorized = await client.PostAsJsonAsync("/payments/transport/bank-webhook", bankEvent);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.False(await context.Set<TransportBankReceipt>().AnyAsync());
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Apikey {PlatformHttpTestHost.BankWebhookKey}");
            for (var duplicate = 0; duplicate < 2; duplicate++)
            {
                using var response = await client.PostAsJsonAsync("/payments/transport/bank-webhook", bankEvent);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("{\"success\":true}", await response.Content.ReadAsStringAsync());
            }
            Assert.Single(await context.Set<TransportBankReceipt>().ToListAsync());
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    private static string TransportFormValue(string form, string name)
    {
        var match = Regex.Match(form, $"name=\"{name}\" value=\"([^\"]+)\"");
        Assert.True(match.Success, $"Missing form field {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [SqlFact]
    public async Task TransportPrepay_UsesStockOrigin_ReassignsAfterTimeout_AndRefundsOnlyMissedPickup()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var (transport, checkout) = await PrepareTransport(fixture);
            var firstShipper = await CreateUserAsync(context, "closest");
            var secondShipper = await CreateUserAsync(context, "next");
            await transport.ConfigureShipper(fixture.Admin, firstShipper.UserId, firstShipper.Email, true);
            await transport.ConfigureShipper(fixture.Admin, secondShipper.UserId, secondShipper.Email, true);
            var preview = await checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            Assert.Equal(new[] { 21_000m, 8_500m }, preview.Sellers.Select(s => s.ShippingVnd));
            Assert.Equal(409_500m, preview.Total);

            await transport.SetLocation(fixture.Admin, 1, "Changed origin", 10.773, 106.7);
            await Assert.ThrowsAsync<ShopRuleException>(() => checkout.Place(fixture.Buyer, preview.Quote));
            await transport.SetLocation(fixture.Admin, 1, "HKShop storage", 10.770, 106.7);
            var orderId = await checkout.Place(fixture.Buyer, preview.Quote);
            var details = (await fixture.Orders.Details(fixture.Buyer, orderId))!;
            var first = details.Order.SellerOrders.Single(s => s.SellerId == Seller.FirstPartyId);
            var second = details.Order.SellerOrders.Single(s => s.SellerId == fixture.Seller2Id);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Orders.StartPackingSellerOrder(fixture.Seller1Owner, first.Id, first.SellerId));
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(details.Order.Number, "TRA-SEPAY", preview.Total, "VND", true)));
            await Pack(fixture, transport, fixture.Seller1Owner, first);
            await Pack(fixture, transport, fixture.Seller2Owner, second);

            await transport.RequestPickup(fixture.Seller1Owner, first.SellerId, first.Id);
            await transport.Tick(CancellationToken.None);
            Assert.Equal(TransportStage.WaitingForShipper,
                (await fixture.Orders.Details(fixture.Buyer, orderId))!.Order.SellerOrders.First().Transport!.Stage);
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            await transport.UpdatePresence(firstShipper, true, 10.769, 106.7);
            await transport.UpdatePresence(secondShipper, true, 10.766, 106.7);
            await transport.Tick(CancellationToken.None);
            var timedOut = await transport.ShipperJob(firstShipper);
            Assert.NotNull(timedOut.AssignmentId);
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(10);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                transport.Respond(firstShipper, first.Id, timedOut.AssignmentId!.Value, true));
            await transport.UpdatePresence(secondShipper, true, 10.766, 106.7);
            await transport.Tick(CancellationToken.None);
            Assert.Null((await transport.ShipperJob(firstShipper)).AssignmentId);
            var accepted = (await transport.ShipperJob(secondShipper)).AssignmentId!.Value;
            await transport.Respond(secondShipper, first.Id, accepted, true);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Orders.DispatchSellerOrder(fixture.Seller1Owner, first.Id, "Other carrier", "tracking", first.SellerId));
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                transport.ConfirmPickup(fixture.Seller1Owner, first.SellerId, first.Id, "WRONG"));
            await transport.ConfirmPickup(fixture.Seller1Owner, first.SellerId, first.Id, TransportService.PickupCode(accepted));
            await transport.ConfirmPickup(fixture.Seller1Owner, first.SellerId, first.Id, TransportService.PickupCode(accepted));
            Assert.False(await transport.Deliver(secondShipper, first.Id, "invalid", null, null));
            Assert.True(await transport.Deliver(secondShipper, first.Id, first.Transport!.DeliveryCode, null, null));
            await transport.RecordShipperPayout(fixture.Admin, first.Id, "SHIPPER-TRANSFER-01");
            await transport.RecordShipperPayout(fixture.Admin, first.Id, "SHIPPER-TRANSFER-01");

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(48);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                transport.RequestPickup(fixture.Seller2Owner, second.SellerId, second.Id));
            await transport.Tick(CancellationToken.None);
            await transport.Tick(CancellationToken.None);
            var final = (await fixture.Orders.Details(fixture.Buyer, orderId))!;
            Assert.Equal(OrderStatus.Delivered, final.Order.Status);
            Assert.Equal(TransportStage.Cancelled, final.Order.SellerOrders.Single(s => s.Id == second.Id).Transport!.Stage);
            var refund = Assert.Single(final.Refunds);
            Assert.Equal((second.Id, 188_500m, 8_500m), (refund.SellerOrderId!.Value, refund.AmountVnd, refund.ShippingVnd));
            var holds = await context.Set<StockReservation>().Where(r => r.OrderId == orderId).ToListAsync();
            Assert.Equal(ReservationStatus.Consumed, holds.Single(r => r.OfferId == fixture.Offer1Id).Status);
            Assert.Equal(ReservationStatus.Released, holds.Single(r => r.OfferId == fixture.Offer2AId).Status);
            Assert.Single(await context.Set<OutboxMessage>().Where(m => m.EventKey.EndsWith($"cancelled-{second.Id:N}")).ToListAsync());
            Assert.Single(await context.Set<LedgerJournal>().Where(l => l.Type == LedgerEventType.ShipperFeePaid).ToListAsync());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task TransportAtDelivery_ScopesCashAndBankVerificationToParcelsAndReleasesSellerAfterRemittance()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var (transport, checkout) = await PrepareTransport(fixture);
            var shipper = await CreateUserAsync(context, "shipper");
            await transport.ConfigureShipper(fixture.Admin, shipper.UserId, shipper.Email, true);
            await transport.UpdatePresence(shipper, true, 10.77, 106.7);
            var preview = await checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.TransportAtDelivery, "");
            var orderId = await checkout.Place(fixture.Buyer, preview.Quote);
            var order = (await fixture.Orders.Details(fixture.Buyer, orderId))!.Order;
            Assert.Empty(order.PaymentAttempts);
            var first = order.SellerOrders.Single(s => s.SellerId == Seller.FirstPartyId);
            var second = order.SellerOrders.Single(s => s.SellerId == fixture.Seller2Id);
            await Pack(fixture, transport, fixture.Seller1Owner, first);
            await Pack(fixture, transport, fixture.Seller2Owner, second);
            await transport.RequestPickup(fixture.Seller2Owner, second.SellerId, second.Id);
            await transport.Tick(CancellationToken.None);
            await transport.RequestPickup(fixture.Seller1Owner, first.SellerId, first.Id);
            await transport.Tick(CancellationToken.None);
            Assert.Single(await context.Set<TransportAssignment>().Where(a => a.EndedAt == null).ToListAsync());

            await Pickup(transport, fixture.Seller2Owner, shipper, second);
            Assert.True(await transport.Deliver(shipper, second.Id, second.Transport!.DeliveryCode, DoorPayment.Cash, second.TotalVnd));
            Assert.True(await transport.Deliver(shipper, second.Id, second.Transport!.DeliveryCode, DoorPayment.Cash, second.TotalVnd));
            Assert.Single(await transport.ShipperRemittances(shipper));
            var ledger = new SettlementLedger(fixture.Store, fixture.Clock, new ShopSettings { SettlementHoldDays = 0 });
            Assert.Equal(0, await ledger.ReleaseDue());
            await transport.RecordBankReceipt("WRONG", TransportService.RemittanceReference(second.Id), 179_000);
            Assert.Single(await transport.BankExceptions(fixture.Admin));
            await transport.RecordBankReceipt("REMIT", TransportService.RemittanceReference(second.Id), 180_000);
            await transport.RecordBankReceipt("REMIT", TransportService.RemittanceReference(second.Id), 180_000);
            Assert.Empty(await transport.ShipperRemittances(shipper));
            Assert.Equal(1, await ledger.ReleaseDue());
            await Assert.ThrowsAsync<ShopRuleException>(() => transport.RecordShipperPayout(fixture.Admin, second.Id, "DOUBLE"));

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            await transport.UpdatePresence(shipper, true, 10.77, 106.7);
            await transport.Tick(CancellationToken.None);
            await Pickup(transport, fixture.Seller1Owner, shipper, first);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                transport.Deliver(shipper, first.Id, first.Transport!.DeliveryCode, DoorPayment.BankTransfer, null));
            await transport.RecordBankReceipt("DOOR", "", first.TotalVnd);
            await transport.ResolveBankReceipt(fixture.Admin, "DOOR", TransportService.DoorReference(first.Id), null);
            Assert.True(await transport.Deliver(shipper, first.Id, first.Transport!.DeliveryCode, DoorPayment.BankTransfer, null));
            await transport.RecordShipperPayout(fixture.Admin, first.Id, "SHIPPER-TRANSFER-02");

            var final = (await fixture.Orders.Details(fixture.Buyer, orderId))!.Order;
            Assert.Equal((OrderStatus.Delivered, PaymentStatus.Paid), (final.Status, final.PaymentStatus));
            Assert.Equal(2, final.PaymentAttempts.Count);
            Assert.All(final.PaymentAttempts, a => Assert.NotNull(a.SellerOrderId));
            Assert.Equal(final.TotalVnd, final.PaymentAttempts.Sum(a => a.CapturedAmountVnd));
            var secondParcel = final.SellerOrders.Single(s => s.Id == second.Id).Transport!;
            Assert.Equal((8_500m, 188_500m), (secondParcel.ShipperFeeVnd, secondParcel.CashCollectedVnd));
            Assert.Equal(3, await context.Set<TransportBankReceipt>().CountAsync());
            Assert.Equal(2, await context.Set<LedgerJournal>().CountAsync(l => l.Type == LedgerEventType.ShipperFeePaid));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<(TransportService Transport, CheckoutService Checkout)> PrepareTransport(MarketplaceFixture fixture)
    {
        var router = new FixtureRoutes();
        var transportSettings = new TransportSettings { Enabled = true };
        var transport = new TransportService(fixture.Store, fixture.Clock, router, new ShippingCommerceCallbackAdapter());
        var shippingGateway = new ShippingGatewayAdapter(transport, transportSettings, router);
        await transport.SetLocation(fixture.Admin, 1, "HKShop storage", 10.770, 106.7);
        await transport.SetLocation(fixture.Seller2Owner, fixture.Seller2Id, "Partner storage", 10.780, 106.7);
        await new AddressService(fixture.Store).Save(fixture.Buyer.UserId, fixture.AddressId,
            "Nguyen Buyer", "0901234567", "123 Le Loi", 10.790, 106.7);
        await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, false);
        await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, false);
        return (transport, new CheckoutService(fixture.Store, new ShopSettings(), fixture.Clock,
            transportSettings, router, shippingGateway));
    }

    [SqlFact]
    public async Task TransportReturnToSender_RefundsVerifiedDoorTransferAfterSellerReceivesParcel()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);
            var (transport, checkout) = await PrepareTransport(fixture);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 0, false);
            var shipper = await CreateUserAsync(context, "return-shipper");
            await transport.ConfigureShipper(fixture.Admin, shipper.UserId, shipper.Email, true);
            await transport.UpdatePresence(shipper, true, 10.77, 106.7);
            var preview = await checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.TransportAtDelivery, "");
            var id = await checkout.Place(fixture.Buyer, preview.Quote);
            var order = (await fixture.Orders.Details(fixture.Buyer, id))!.Order;
            var parcel = Assert.Single(order.SellerOrders);
            var item = Assert.Single(order.Items);
            await Pack(fixture, transport, fixture.Seller2Owner, parcel);
            await transport.RequestPickup(fixture.Seller2Owner, parcel.SellerId, parcel.Id);
            await transport.Tick(CancellationToken.None);
            await Pickup(transport, fixture.Seller2Owner, shipper, parcel);

            await transport.RecordBankReceipt("RETURN-DEPOSIT", TransportService.DoorReference(parcel.Id), parcel.TotalVnd);
            await transport.ReturnToSender(shipper, parcel.Id, "Buyer unavailable to receive the parcel.");
            Assert.Empty((await fixture.Orders.Details(fixture.Buyer, id))!.Refunds);
            await fixture.Orders.ReceiveUndeliveredSellerOrder(fixture.Seller2Owner, parcel.Id,
                "Returned in sellable condition", [new ReturnQuantity(item.Id, 1)], parcel.SellerId);

            var details = (await fixture.Orders.Details(fixture.Buyer, id))!;
            var refund = Assert.Single(details.Refunds);
            Assert.Equal((parcel.TotalVnd, parcel.ShippingVnd, RefundStatus.Pending),
                (refund.AmountVnd, refund.ShippingVnd, refund.Status));
            await fixture.Payments.CompleteRefund(fixture.Admin, refund.Id, "RETURN-BANK-REFUND-01");
            var final = (await fixture.Orders.Details(fixture.Buyer, id))!;
            Assert.Equal(RefundStatus.Completed, Assert.Single(final.Refunds).Status);
            Assert.Equal(TransportStage.Returned, Assert.Single(final.Order.SellerOrders).Transport!.Stage);
            Assert.Null((await transport.ShipperJob(shipper)).AssignmentId);
            Assert.Equal(10, await context.Set<InventoryBalance>().Where(b => b.OfferId == fixture.Offer2AId)
                .Select(b => b.OnHand).SingleAsync());
            Assert.False(await context.Set<LedgerJournal>().AnyAsync(l => l.Type == LedgerEventType.ShipperFeeEarned));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static async Task Pack(MarketplaceFixture fixture, TransportService transport, ShopActor seller, SellerOrder child)
    {
        await fixture.Orders.StartPackingSellerOrder(seller, child.Id, child.SellerId);
        await transport.MarkPacked(seller, child.SellerId, child.Id);
    }

    private static async Task Pickup(TransportService transport, ShopActor seller, ShopActor shipper, SellerOrder child)
    {
        var job = await transport.ShipperJob(shipper);
        Assert.Equal(child.Id, job.Job!.Parcel.SellerOrderId);
        var assignmentId = job.AssignmentId!.Value;
        await transport.Respond(shipper, child.Id, assignmentId, true);
        await transport.ConfirmPickup(seller, child.SellerId, child.Id, TransportService.PickupCode(assignmentId));
    }

    private sealed class FixtureRoutes : ITransportRouting
    {
        public Task<long?> Distance(TransportPoint origin, TransportPoint destination, CancellationToken token = default) =>
            Task.FromResult<long?>(destination.Latitude == 10.790
                ? (origin.Latitude == 10.770 ? 4200 : 1700)
                : (long)origin.DistanceTo(destination));
    }
}
