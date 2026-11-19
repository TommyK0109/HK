using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Application.Transportation;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Gateway;
using HKShop.Identity.Api.Domain;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Messaging;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Payments.Api.Infrastructure;
using HKShop.ServiceDefaults;
using HKShop.Shipping.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace HKShop.ContractTests;

public sealed class SqlContractFactAttribute : FactAttribute
{
    public SqlContractFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL")))
            Skip = "Set HKSHOP_TEST_SQL to execute live HTTP client-to-service contract tests.";
    }
}

public sealed class ContractTests
{
    private const string TestSigningKey = "hkshop-phase3-contract-test-signing-key-32bytes!";
    private static readonly JwtSettings TestJwt = new()
    {
        Issuer = "https://hkshop.identity",
        Audience = "hkshop.services",
        SigningKey = TestSigningKey
    };

    [Fact]
    public void ServiceEvents_RoundTripInsideEventEnvelopeWithoutLoss()
    {
        var now = new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero);
        var orderId = Guid.NewGuid();
        var sellerOrderId = Guid.NewGuid();
        var line = new StockReservationLine(10, 20, 1, 2, 150_000m, sellerOrderId);
        var parcel = new NewTransportParcel(sellerOrderId, 1, "1 Le Loi", new(10.77, 106.70), new(10.78, 106.71), 2400, "123456");

        AssertEnvelopeRoundTrip(new OrderConfirmed("HK123", "buyer@example.test"));
        AssertEnvelopeRoundTrip(new UserRegistered("u1", "u1@example.test", "User One", "https://shop/confirm", now));
        AssertEnvelopeRoundTrip(new EmailConfirmationRequested("u1", "u1@example.test", "https://shop/confirm", now));
        AssertEnvelopeRoundTrip(new PasswordResetRequested("u1", "u1@example.test", "https://shop/reset", now));
        AssertEnvelopeRoundTrip(new UserDisabled("u1", "u1@example.test", now));
        AssertEnvelopeRoundTrip(new UserRolesChanged("u1", "u1@example.test", ["Shipper"], true, now));
        AssertEnvelopeRoundTrip(new StockReserved(orderId, [line], now));
        AssertEnvelopeRoundTrip(new StockReservationFailed(orderId, "Out of stock", now));
        AssertEnvelopeRoundTrip(new StockReleased(orderId, sellerOrderId, "Cancelled", now));
        AssertEnvelopeRoundTrip(new OfferChanged(10, 20, 1, "HKShop", "SKU-1", "shirt", "Shirt", 150_000m, 5, true, now));
        AssertEnvelopeRoundTrip(new SellerStatusChanged(2, "Partner Store", SellerStatus.Active, now));
        AssertEnvelopeRoundTrip(new SellerMembershipChanged(2, "Partner Store", SellerStatus.Active, "u2", "u2@example.test", SellerRole.Owner, SellerMembershipStatus.Active, now));
        AssertEnvelopeRoundTrip(new SellerLocationChanged(2, 5, "Warehouse", "12 Tran Hung Dao", 10.77, 106.70, true, now));
        AssertEnvelopeRoundTrip(new SellerInvited(7, 2, "Partner Store", "inv@example.test", "https://shop/inv", now));
        AssertEnvelopeRoundTrip(new OrderPlacementRequested(orderId, "HK123", "u1", [line], now));
        AssertEnvelopeRoundTrip(new PaymentRequested(orderId, "HK123", "u1", "u1@example.test", PaymentMethod.SePay, 330_000m, now.AddMinutes(15),
            [new PaymentSellerSplit(sellerOrderId, 1, "HK123-S1", 300_000m, 0m, 0m, 30_000m, 330_000m, 0m)], now));
        AssertEnvelopeRoundTrip(new OrderCancelled(orderId, "HK123", "u1@example.test", "Buyer request", [sellerOrderId], now));
        AssertEnvelopeRoundTrip(new SellerOrderPlaced(sellerOrderId, orderId, 1, "HK123-S1", OrderStatus.Confirmed, 330_000m, 30_000m, PaymentMethod.SePay, PaymentStatus.Pending, "Buyer", "0901234567", "123 Le Loi", parcel, now));
        AssertEnvelopeRoundTrip(new SellerOrderPacked(sellerOrderId, orderId, 1, "HK123-S1", now));
        AssertEnvelopeRoundTrip(new SellerOrderDispatched(sellerOrderId, orderId, 1, "HK123-S1", "u1@example.test", "VNPost", "TRK1", [line], now));
        AssertEnvelopeRoundTrip(new SellerOrderDelivered(sellerOrderId, orderId, 1, "HK123-S1", "u1", "u1@example.test", [42], now));
        AssertEnvelopeRoundTrip(new ReturnInspected(Guid.NewGuid(), orderId, sellerOrderId, 1, [line], now));
        AssertEnvelopeRoundTrip(new RefundRequested(orderId, sellerOrderId, 1, null, "key-1", 150_000m, 0m, [new RefundLineAllocationDto(1, 1, 150_000m, 0m, 0m)], now));
        AssertEnvelopeRoundTrip(new PaymentCaptured(Guid.NewGuid(), orderId, "HK123", 330_000m, "TX-1", now));
        AssertEnvelopeRoundTrip(new PaymentFailed(Guid.NewGuid(), orderId, "HK123", "u1@example.test", "Declined", now));
        AssertEnvelopeRoundTrip(new PaymentExpired(orderId, "HK123", "u1@example.test", now));
        AssertEnvelopeRoundTrip(new RefundCompletedEvent(9, orderId, "HK123", "u1@example.test", sellerOrderId, Guid.NewGuid(), 150_000m, 0m, true, "SIM-REF", now));
        AssertEnvelopeRoundTrip(new PayoutApproved(Guid.NewGuid(), 2, 100_000m, now));
        AssertEnvelopeRoundTrip(new PayoutPaid(Guid.NewGuid(), 2, 100_000m, "SIM-PAYOUT", now));
        AssertEnvelopeRoundTrip(new ParcelPickedUp(sellerOrderId, orderId, 1, "HK123-S1", "shipper-1", now));
        AssertEnvelopeRoundTrip(new ParcelDelivered(sellerOrderId, orderId, 1, "shipper-1", DoorPayment.Cash, false, 30_000m, now));
        AssertEnvelopeRoundTrip(new DoorPaymentVerified(sellerOrderId, orderId, 1, 330_000m, 30_000m, "BANK-1", true, now));
        AssertEnvelopeRoundTrip(new PickupDeadlineExpired(sellerOrderId, orderId, 1, now));
        AssertEnvelopeRoundTrip(new ParcelReturnedToSender(sellerOrderId, orderId, 1, "No answer", now));
        AssertEnvelopeRoundTrip(new ShipperFeeDue(sellerOrderId, orderId, 30_000m, "PAY-1", "admin-1", now));
    }

    [Fact]
    public async Task ServiceHttpClient_TranslatesProblemDetailsToShopRuleExceptionAnd404ToNull()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddHKShopDefaults("HKShop.ContractProbe");
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapGet("/internal/ordering/orders/{id:guid}", () => Results.NotFound());
        app.MapPost("/internal/ordering/orders/{id:guid}/cancel", () =>
        {
            throw new ShopRuleException("Cannot cancel dispatched order.");
        });
        await app.StartAsync();

        var client = new OrderingServiceClient(app.GetTestClient());
        Assert.Null(await client.GetOrderStatusAsync(Guid.NewGuid()));
        var error = await Assert.ThrowsAsync<ShopRuleException>(() => client.CancelOrderAsync(Guid.NewGuid(), "Changed mind"));
        Assert.Equal("Cannot cancel dispatched order.", error.Message);
    }

    [Fact]
    public async Task Gateway_RoutesMediaToCatalogClusterAlongsideWebPaymentsAndShipping()
    {
        var calls = new List<(string Authority, string Path)>();
        using var factory = new WebApplicationFactory<GatewayAssemblyMarker>().WithWebHostBuilder(web => web
            .UseSetting("Destinations:Web", "http://web.internal")
            .UseSetting("Destinations:Catalog", "http://catalog.internal")
            .UseSetting("Destinations:Payments", "http://payments.internal")
            .UseSetting("Destinations:Shipping", "http://shipping.internal")
            .UseSetting("HttpsRedirection:Enabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IForwarderHttpClientFactory>(new RecordingForwarder(calls))));

        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/media/sample.png")).StatusCode);
        Assert.Equal(("http://catalog.internal", "/media/sample.png"), calls.Last());
    }

    [SqlContractFact]
    public async Task TypedClients_RoundTripLiveEndpointsAcrossAllFiveApiServices()
    {
        var connections = new[] { "Identity", "Catalog", "Shipping", "Payments", "Ordering" }
            .ToDictionary(name => name, name => NewTestConnection($"Contract_{name}"));
        var tempDir = Directory.CreateTempSubdirectory("hkshop-contract-");
        try
        {
            await using var identityApp = Host<IdentityServiceDbContext>("Identity", connections["Identity"], tempDir.FullName);
            await using var catalogApp = Host<CatalogDbContext>("Catalog", connections["Catalog"], tempDir.FullName);
            await using var shippingApp = Host<ShippingDbContext>("Shipping", connections["Shipping"], tempDir.FullName);
            await using var paymentsApp = Host<PaymentsDbContext>("Payments", connections["Payments"], tempDir.FullName);
            await using var orderingApp = Host<OrderingDbContext>("Ordering", connections["Ordering"], tempDir.FullName, services =>
            {
                services.AddHttpClient<IdentityServiceClient>().ConfigurePrimaryHttpMessageHandler(identityApp.Server.CreateHandler);
                services.AddHttpClient<CatalogServiceClient>().ConfigurePrimaryHttpMessageHandler(catalogApp.Server.CreateHandler);
                services.AddHttpClient<ShippingServiceClient>().ConfigurePrimaryHttpMessageHandler(shippingApp.Server.CreateHandler);
            });

            var adminJwt = ShopActorFactory.CreateJwtToken(
                new ShopActor("admin-1", "admin@example.test", true, Staff: true, Admin: true) { Mfa = true, HasEmployeeRole = true },
                TestJwt);

            // 1. Identity contract
            using var idHttp = identityApp.CreateClient();
            var idClient = new IdentityServiceClient(idHttp);
            const string buyerEmail = "contract-buyer@example.test";
            const string buyerPassword = "ContractPassword_123!";
            await idClient.RegisterAsync(new(buyerEmail, "Contract Buyer", buyerPassword));

            // Confirm email directly so we can exercise token issuance, refresh, addresses and revocation.
            await using (var idDb = new IdentityServiceDbContext(new DbContextOptionsBuilder<IdentityServiceDbContext>().UseSqlServer(connections["Identity"]).Options))
            {
                var user = await idDb.Users.SingleAsync(u => u.Email == buyerEmail);
                user.EmailConfirmed = true;
                await idDb.SaveChangesAsync();
            }

            var issued = await idClient.IssueTokenAsync(new(buyerEmail, buyerPassword, "commerce"));
            Assert.False(issued.RequiresTwoFactor);
            Assert.False(string.IsNullOrWhiteSpace(issued.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(issued.RefreshToken));

            var refreshed = await idClient.RefreshTokenAsync(issued.RefreshToken!);
            Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
            Assert.NotEqual(issued.RefreshToken, refreshed.RefreshToken);

            idHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
            var addressId = await idClient.SaveCustomerAddressAsync(new(0, "Contract Buyer", "0901234567", "10 Le Loi", 10.77, 106.70));
            var addresses = await idClient.GetCustomerAddressesAsync();
            Assert.Single(addresses);
            var fetchedAddress = await idClient.GetCustomerAddressAsync(addressId);
            Assert.Equal("Contract Buyer", fetchedAddress?.Recipient);

            // 2. Catalog contract
            using var catHttp = catalogApp.CreateClient();
            catHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            var catClient = new CatalogServiceClient(catHttp);
            var catId = await catClient.SaveCategoryAsync(new(0, "Apparel", true));
            Assert.NotEmpty(await catClient.CategoriesAsync());
            var prodId = await catClient.SaveProductAsync(new(0, "Cotton Tee", "cotton-tee", "Soft cotton", catId));
            var skuId = await catClient.SaveSkuAsync(new(prodId, 0, "TEE-M", "M", "White", 200_000m, true));
            await catClient.AdjustFirstPartyStockAsync(new(skuId, 10, 1, "Initial stock"));
            await using (var png = new MemoryStream(MinimalPng()))
                await catClient.AddImageAsync(prodId, "Front view", png);
            var editDetails = await catClient.EditDetailsAsync(prodId);
            Assert.Single(editDetails.Images);
            using var mediaResp = await catHttp.GetAsync($"/media/{editDetails.Images[0].FileName}");
            Assert.Equal(HttpStatusCode.OK, mediaResp.StatusCode);
            await catClient.ChangePublicationAsync(prodId, true);
            Assert.Equal(1, (await catClient.ProductsAsync(1)).Total);
            Assert.Equal(1, (await catClient.ListFirstPartyStockAsync(false, 1)).Total);
            Assert.NotEmpty(await catClient.FirstPartyHistoryAsync(skuId));

            var cartOffers = await catClient.CartOffersAsync(new([], skuId));
            var offer = Assert.Single(cartOffers.Offers);
            Assert.True(offer.Active);
            Assert.Equal(10, offer.Available);
            var quoted = await catClient.QuoteOffersAsync(new([offer.OfferId]));
            Assert.Single(quoted);

            var probeOrderId = Guid.NewGuid();
            var reserved = await catClient.ReserveStockAsync(new(probeOrderId, [new(offer.OfferId, skuId, 1, 2, 200_000m)]));
            Assert.True(reserved.Reserved);
            await catClient.ReleaseStockAsync(new(probeOrderId, null, "Contract cleanup"));
            Assert.NotEmpty(await catClient.AuditAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));

            // 3. Shipping contract
            using var shipHttp = shippingApp.CreateClient();
            shipHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            var shipClient = new ShippingServiceClient(shipHttp);
            await shipClient.SetSellerLocationAsync(1, new("1 Nguyen Hue", 10.775, 106.701));
            var loc = await shipClient.GetSellerLocationAsync(1);
            Assert.NotNull(loc);
            Assert.Equal("1 Nguyen Hue", loc.PickupAddress);
            var sampleSoId = Guid.NewGuid();
            var registered = await shipClient.RegisterParcelsAsync(new([new NewTransportParcel(sampleSoId, loc.Id, loc.PickupAddress!, new(10.775, 106.701), new(10.78, 106.71), 1500, "654321")]));
            Assert.True(registered.ContainsKey(sampleSoId));
            var summaries = await shipClient.GetParcelSummariesAsync(new([sampleSoId], true));
            Assert.Equal("", summaries[sampleSoId].DeliveryCode);
            Assert.False(await shipClient.HasUnverifiedCashRemittanceAsync(sampleSoId));
            await shipClient.CancelOrderParcelsAsync(sampleSoId, DateTimeOffset.UtcNow);
            Assert.Empty(await shipClient.OperationsAsync());
            Assert.Empty(await shipClient.BankExceptionsAsync());
            Assert.NotEmpty(await shipClient.AuditAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));

            // 4. Payments contract
            using var payHttp = paymentsApp.CreateClient();
            payHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            var payClient = new PaymentsServiceClient(payHttp);
            var payCfg = await payClient.GetConfigAsync();
            Assert.True(payCfg.OnlineAvailable);
            Assert.Null(await payClient.GetOrderPaymentDetailsAsync(Guid.NewGuid()));
            Assert.Empty(await payClient.GetIssuesAsync());
            Assert.Empty(await payClient.GetPendingRefundsAsync());
            Assert.Equal(0m, (await payClient.GetSellerBalancesAsync(1)).AvailableVnd);
            Assert.Equal(0, (await payClient.GetPayoutQueueAsync(1)).Total);
            Assert.Empty(await payClient.AuditAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));

            // 5. Ordering contract (guest cart -> merge -> quote -> place -> details -> cancel)
            using var ordHttp = orderingApp.CreateClient();
            var ordClient = new OrderingServiceClient(ordHttp);
            var guestHash = new string('a', 64);
            Assert.Equal(2, await ordClient.ChangeCartAsync(new(guestHash, offer.OfferId, null, 2, false)));
            Assert.Single((await ordClient.CartAsync(guestHash)).Lines);

            ordHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
            Assert.Equal(0, await ordClient.MergeCartAsync(guestHash));
            var buyerCart = await ordClient.CartAsync(null);
            Assert.Equal(2, Assert.Single(buyerCart.Lines).Quantity);

            var preview = await ordClient.QuoteAsync(new(addressId, PaymentMethod.Cod, ""));
            Assert.Equal(400_000m, preview.Subtotal);
            var placed = await ordClient.PlaceOrderAsync(preview.Quote);
            Assert.NotEqual(Guid.Empty, placed.OrderId);
            Assert.Equal(1, (await ordClient.ListMyOrdersAsync(null, 1)).Total);
            var status = await ordClient.GetOrderStatusAsync(placed.OrderId);
            Assert.Equal(placed.OrderId, status?.OrderId);
            var details = await ordClient.GetOrderDetailsAsync(placed.OrderId);
            Assert.Single(details!.SellerOrders);
            Assert.Single(details.Items);
            await ordClient.CancelOrderAsync(placed.OrderId, "Contract cancel");
            Assert.Equal(OrderStatus.Cancelled, (await ordClient.GetOrderStatusAsync(placed.OrderId))?.Status);

            ordHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
            Assert.Equal(1, (await ordClient.ListOrdersAsync(OrderStatus.Cancelled, 1)).Total);
            Assert.NotNull(await ordClient.GetSalesSummaryAsync(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow)));

            // Address deletion & token revocation
            await idClient.DeleteCustomerAddressAsync(addressId);
            Assert.Empty(await idClient.GetCustomerAddressesAsync());
            await idClient.RevokeTokenAsync(refreshed.RefreshToken!);
            await Assert.ThrowsAsync<ShopRuleException>(() => idClient.RefreshTokenAsync(refreshed.RefreshToken!));
        }
        finally
        {
            foreach (var conn in connections.Values)
            {
                await using var db = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(conn).Options);
                await db.Database.EnsureDeletedAsync();
            }
            tempDir.Delete(recursive: true);
        }
    }

    private static void AssertEnvelopeRoundTrip<T>(T payload)
    {
        var envelope = new EventEnvelope(
            Guid.NewGuid(),
            typeof(T).Name,
            1,
            typeof(T).Name,
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "corr-1",
            null,
            JsonSerializer.SerializeToElement(payload));
        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<EventEnvelope>(json)!;
        Assert.Equal(envelope.EventId, restored.EventId);
        Assert.Equal(typeof(T).Name, restored.Type);
        var inner = restored.Payload.Deserialize<T>();
        Assert.NotNull(inner);
    }

    private static string NewTestConnection(string suffix)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL"))
        {
            InitialCatalog = $"HKShopTests_{suffix}_{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
    }

    private static WebApplicationFactory<T> Host<T>(
        string service, string connection, string rootDir, Action<IServiceCollection>? configure = null) where T : class =>
        new WebApplicationFactory<T>().WithWebHostBuilder(web => web
            .UseSetting($"ConnectionStrings:{service}", connection)
            .UseSetting("Jwt:Issuer", TestJwt.Issuer)
            .UseSetting("Jwt:Audience", TestJwt.Audience)
            .UseSetting("Jwt:SigningKey", TestJwt.SigningKey)
            .UseSetting("Shop:KeysPath", Path.Combine(rootDir, "keys"))
            .UseSetting("Shop:MediaPath", Path.Combine(rootDir, "media"))
            .UseSetting("Shop:PublicOrigin", "https://shop.example.test")
            .UseSetting("SePay:MerchantId", "test")
            .UseSetting("SePay:MerchantSecret", "test")
            .UseSetting("SePay:IpnSecret", "test")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(new OutboxRelayOptions("", "", service));
                services.AddSingleton(new InboxConsumerOptions("", "", service, []));
                configure?.Invoke(services);
            }));

    private static byte[] MinimalPng() =>
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed class RecordingForwarder(List<(string Authority, string Path)> calls) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(calls), true);

        private sealed class Handler(List<(string Authority, string Path)> calls) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                calls.Add((request.RequestUri!.GetLeftPart(UriPartial.Authority), request.RequestUri.AbsolutePath));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
