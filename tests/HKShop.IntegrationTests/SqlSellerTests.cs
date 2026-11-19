using HKShop.Application;
using HKShop.Application.Orders;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class SqlSellerTests
{
    private const string Origin = "https://shop.example.test";

    [SqlFact]
    public async Task MembershipIsCheckedForEachSellerAndEachOperation()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var store = new SqlShopStore(factory);
            var service = new SellerService(store, TimeProvider.System);
            var applications = StoreApplicationFixture.Service(store, TimeProvider.System);
            var admin = await User(context, "admin", admin: true);
            var ownerA = await User(context, "owner-a");
            var ownerB = await User(context, "owner-b");
            var shared = await User(context, "both-sellers");
            await Assert.ThrowsAsync<ShopRuleException>(() => StoreApplicationFixture.Submit(applications, ownerA with { Verified = false }, "Seller A"));
            var applicationA = await StoreApplicationFixture.Submit(applications, ownerA, "Seller A");
            var applicationB = await StoreApplicationFixture.Submit(applications, ownerB, "Seller B");

            Assert.Empty(await service.Mine(ownerA));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Mine(ownerA with { Mfa = false }));
            await Assert.ThrowsAsync<ShopRuleException>(() => applications.Decide(ownerA, applicationA, true, null));
            await applications.Decide(admin, applicationA, true, null);
            await applications.Decide(admin, applicationB, true, null);
            var sellerA = (await applications.Details(admin, applicationA))!.Summary.SellerId!.Value;
            var sellerB = (await applications.Details(admin, applicationB))!.Summary.SellerId!.Value;
            Assert.Contains((await service.PlatformList(admin, 1)).Items, seller => seller.Id == sellerA);

            Assert.Equal(SellerRole.Owner, (await service.Workspace(ownerA, sellerA)).Role);
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Workspace(ownerA with { Mfa = false }, sellerA));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Workspace(ownerA, sellerB));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Team(ownerA, sellerB));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Invite(ownerA, sellerB, shared.Email, SellerRole.Finance, Origin));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.RevokeMember(ownerA, sellerB, shared.UserId));

            await service.Invite(ownerA, sellerA, shared.Email, SellerRole.CatalogManager, Origin);
            Assert.Equal(sellerA, await service.Accept(shared, await InviteToken(context, shared.Email)));
            await service.Invite(ownerB, sellerB, shared.Email, SellerRole.Fulfillment, Origin);
            Assert.Equal(sellerB, await service.Accept(shared, await InviteToken(context, shared.Email)));

            Assert.Equal(new[] { sellerA, sellerB }.Order(), (await service.Mine(shared)).Select(s => s.Id).Order());
            var catalog = await service.Workspace(shared, sellerA);
            var fulfillment = await service.Workspace(shared, sellerB);
            Assert.Equal(SellerRole.CatalogManager, catalog.Role);
            Assert.True(catalog.Allows(SellerPermission.Catalog));
            Assert.False(catalog.Allows(SellerPermission.Fulfillment));
            Assert.Equal(SellerRole.Fulfillment, fulfillment.Role);
            Assert.True(fulfillment.Allows(SellerPermission.Fulfillment));
            Assert.False(fulfillment.Allows(SellerPermission.Catalog));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Team(shared, sellerA));

            await service.RevokeMember(ownerA, sellerA, shared.UserId);
            Assert.Equal(sellerB, Assert.Single(await service.Mine(shared)).Id);
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Workspace(shared, sellerA));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.RevokeMember(ownerA, sellerA, ownerB.UserId));
            await service.SetStatus(admin, sellerB, SellerStatus.Suspended, "Review required");
            Assert.Equal(SellerStatus.Suspended, Assert.Single(await service.Mine(shared)).Status);
            Assert.False((await service.Workspace(shared, sellerB)).Allows(SellerPermission.Fulfillment));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Team(ownerB, sellerB));
            await service.SetStatus(admin, sellerB, SellerStatus.Active, "Review completed");
            Assert.Equal(sellerB, (await service.Workspace(shared, sellerB)).Id);
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task InvitationsRequireMatchingVerifiedMfaAndCanBeUsedOnlyOnce()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var clock = new TestClock();
            var service = new SellerService(new SqlShopStore(factory), clock);
            var admin = await User(context, "admin", admin: true);
            var owner = await User(context, "owner");
            var invitee = await User(context, "invitee");
            var another = await User(context, "another");
            var seller = await StoreApplicationFixture.Open(new SqlShopStore(factory), clock, admin, owner, "Invite test");
            var anotherSeller = await StoreApplicationFixture.Open(new SqlShopStore(factory), clock, admin, another, "Other seller");

            await service.Invite(owner, seller, invitee.Email, SellerRole.Finance, Origin);
            var token = await InviteToken(context, invitee.Email);
            var invitation = Assert.Single((await service.Team(owner, seller)).Invitations);
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.RevokeInvitation(another, anotherSeller, invitation.Id));
            await Assert.ThrowsAsync<ShopRuleException>(() => service.Accept(another, token));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Accept(invitee with { Verified = false }, token));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Accept(invitee with { Mfa = false }, token));
            var attempts = await Task.WhenAll(AcceptOnce(token), AcceptOnce(token));
            Assert.Equal(seller, Assert.Single(attempts, result => result is not null));
            Assert.Single(attempts, result => result is null);
            Assert.Equal(SellerRole.Finance, (await service.Workspace(invitee, seller)).Role);
            await Assert.ThrowsAsync<ShopRuleException>(() => service.Accept(invitee, token));

            await service.RevokeMember(owner, seller, invitee.UserId);
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => service.Workspace(invitee, seller));
            await service.Invite(owner, seller, invitee.Email, SellerRole.Finance, Origin);
            token = await InviteToken(context, invitee.Email);
            invitation = Assert.Single((await service.Team(owner, seller)).Invitations);
            await service.RevokeInvitation(owner, seller, invitation.Id);
            await Assert.ThrowsAsync<ShopRuleException>(() => service.Accept(invitee, token));

            await service.Invite(owner, seller, invitee.Email, SellerRole.Finance, Origin);
            token = await InviteToken(context, invitee.Email);
            invitation = Assert.Single((await service.Team(owner, seller)).Invitations);
            clock.UtcNow = invitation.ExpiresAt.AddSeconds(1);
            await Assert.ThrowsAsync<ShopRuleException>(() => service.Accept(invitee, token));
            Assert.Empty(await service.Mine(invitee));

            async Task<long?> AcceptOnce(string concurrentToken)
            {
                try { return await service.Accept(invitee, concurrentToken); }
                catch (ShopRuleException) { return null; }
            }
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task ReportsAndExportSourceOnlyIncludeTheAuthorizedSellersData()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var clock = new TestClock();
            var store = new SqlShopStore(factory);
            var service = new SellerService(store, clock);
            var reporting = new ReportingService(store);
            var admin = await User(context, "admin", admin: true);
            var ownerA = await User(context, "owner-a");
            var ownerB = await User(context, "owner-b");
            var financeA = await User(context, "finance-a");
            var sellerA = await StoreApplicationFixture.Open(store, clock, admin, ownerA, "Report A");
            var sellerB = await StoreApplicationFixture.Open(store, clock, admin, ownerB, "Report B");
            await service.Invite(ownerA, sellerA, financeA.Email, SellerRole.Finance, Origin);
            await service.Accept(financeA, await InviteToken(context, financeA.Email));

            var orderA = PaidOrder(ownerA, sellerA, "ORDER-A", 1_000m, clock.UtcNow);
            var orderB = PaidOrder(ownerA, sellerB, "ORDER-B", 9_000m, clock.UtcNow);
            context.AddRange(orderA, orderB);
            context.AddRange(CompletedRefund(orderA, 100m, clock.UtcNow), CompletedRefund(orderB, 900m, clock.UtcNow));
            await context.SaveChangesAsync();

            var date = DateOnly.FromDateTime(ShopSettings.VietnamTime(clock.UtcNow).DateTime);
            var report = await reporting.SellerReport(ownerA, sellerA, date, date, null, 1);
            Assert.Equal((1_000m, 100m, 900m), (report.Sales.Collected, report.Sales.Refunded, report.Sales.Net));
            Assert.Equal(new StatusCount(OrderStatus.Delivered, 1), Assert.Single(report.Sales.Orders));
            var audit = Assert.IsType<Page<SellerAuditEntry>>(report.Audit);
            Assert.NotEmpty(audit.Items);
            Assert.DoesNotContain(audit.Items, row => row.ActorId == ownerB.UserId);
            Assert.Contains(audit.Items, row => row.ActorId == ownerA.UserId);
            Assert.Equal(await context.Set<AuditEntry>().CountAsync(row => row.SellerId == sellerA), audit.Total);
            var financeReport = await reporting.SellerReport(financeA, sellerA, date, date, null, 1);
            Assert.Null(financeReport.Audit);
            var export = await reporting.SellerSales(financeA, sellerA, date, date);
            Assert.Equal((1_000m, 100m), (export.Collected, export.Refunded));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => reporting.SellerReport(ownerA, sellerB, date, date, null, 1));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => reporting.SellerSales(financeA, sellerB, date, date));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => reporting.SellerSales(financeA with { Mfa = false }, sellerA, date, date));

            context.AddRange(Enumerable.Range(0, 27).Select(i => new AuditEntry
            {
                SellerId = sellerA, Action = "Hủy đơn", Target = $"FILTER-{i}", OccurredAt = clock.UtcNow
            }));
            context.Add(new AuditEntry { SellerId = sellerA, Action = "manual cancel", Target = "RAW", OccurredAt = clock.UtcNow });
            context.Add(new AuditEntry { SellerId = sellerB, Action = "Hủy đơn", Target = "OTHER", OccurredAt = clock.UtcNow });
            await context.SaveChangesAsync();

            var firstPage = Assert.IsType<Page<SellerAuditEntry>>((await reporting.SellerReport(ownerA, sellerA, date, date, "cancel", 1)).Audit);
            var secondPage = Assert.IsType<Page<SellerAuditEntry>>((await reporting.SellerReport(ownerA, sellerA, date, date, "cancel", 2)).Audit);
            Assert.Equal(28, firstPage.Total);
            Assert.Equal(25, firstPage.Items.Count);
            Assert.Equal(3, secondPage.Items.Count);
            Assert.DoesNotContain(firstPage.Items.Concat(secondPage.Items), row => row.Target == "OTHER");
            Assert.Contains(firstPage.Items.Concat(secondPage.Items), row => row.Target == "RAW");
            var raw = Assert.IsType<Page<SellerAuditEntry>>((await reporting.SellerReport(ownerA, sellerA, date, date, "manual cancel", 1)).Audit);
            Assert.Equal("RAW", Assert.Single(raw.Items).Target);
            var vietnamese = Assert.IsType<Page<SellerAuditEntry>>((await reporting.SellerReport(ownerA, sellerA, date, date, "Hủy đơn", 1)).Audit);
            Assert.Equal(27, vietnamese.Total);
            Assert.Equal(29, (await reporting.Audit(admin, date, date, "cancel", 1)).Total);

            await service.SetStatus(admin, sellerA, SellerStatus.Suspended, "Review required");
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() => reporting.SellerSales(financeA, sellerA, date, date));
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task LegacyCodOrderIsBackfilledAndPlatformStaffCanStillFulfillIt()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261112090713_AllowMultipleAndEditableProductReviews");
            var clock = new TestClock();
            var buyer = await User(context, "buyer");
            var staff = await User(context, "platform-staff", staff: true);
            var orderId = Guid.NewGuid();
            var status = (int)OrderStatus.Confirmed;
            var method = (int)PaymentMethod.Cod;
            var due = (int)PaymentStatus.Due;
            var now = clock.UtcNow;
            var sku = new Sku { Code = "LEGACY-ONE", Active = true, PriceVnd = 1_000m };
            sku.Stock.Adjust(1);
            sku.Stock.Reserve(1);
            sku.ProductId = await SqlTestDatabase.InsertLegacyProduct(context, "Legacy product", "legacy-product");
            context.Add(sku);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.Orders (Id, Number, CustomerId, CustomerEmail, SubmissionKey, CreatedAt,
                    Status, Recipient, Phone, ShippingAddress, ShippingVnd, TotalVnd)
                VALUES ({orderId}, N'LEGACY-ORDER', {buyer.UserId}, {buyer.Email}, {Guid.NewGuid()}, {now},
                    {status}, N'Buyer', N'0123456789', N'Address', 0, 1000);
                INSERT dbo.Payments (OrderId, Method, Status, AmountVnd, CollectionReference, Issue)
                VALUES ({orderId}, {method}, {due}, 1000, N'', N'');
                INSERT dbo.StockReservations (OrderId, SkuId, Quantity, Status)
                VALUES ({orderId}, {sku.Id}, 1, {(int)ReservationStatus.Held});
                INSERT dbo.AuditEntries (ActorId, Action, Target, Reason, OccurredAt)
                VALUES ({staff.UserId}, N'Đóng gói', N'LEGACY-ORDER', N'Historical audit', {now}),
                       ({staff.UserId}, N'Đăng nhập quản trị', N'LEGACY-ORDER', N'Platform audit', {now});
                """);

            await context.Database.MigrateAsync();
            var orders = new OrderService(new SqlShopStore(factory), clock);
            var before = Assert.IsType<OrderDetails>(await orders.Details(buyer, orderId));
            Assert.Equal(Seller.FirstPartyId, before.Order.SellerId);
            Assert.Equal(OrderStatus.Confirmed, before.Order.Status);
            var historicalAudits = await context.Set<AuditEntry>().Where(row => row.Target == "LEGACY-ORDER").ToListAsync();
            Assert.Equal(Seller.FirstPartyId, Assert.Single(historicalAudits, row => row.Action == "Đóng gói").SellerId);
            Assert.Null(Assert.Single(historicalAudits, row => row.Action == "Đăng nhập quản trị").SellerId);
            await Assert.ThrowsAsync<ShopRuleException>(() => orders.StartPacking(buyer, orderId));
            await orders.StartPacking(staff, orderId);
            await orders.Dispatch(staff, orderId, "Test carrier", "TRACK-1");
            await orders.Deliver(staff, orderId, 1_000m, "COD-1");

            var delivered = Assert.IsType<OrderDetails>(await orders.Details(buyer, orderId));
            Assert.Equal(OrderStatus.Delivered, delivered.Order.Status);
            Assert.Equal(PaymentStatus.Paid, delivered.Order.Payment.Status);
            Assert.Equal("COD-1", delivered.Order.Payment.CollectionReference);
            Assert.Equal(ReservationStatus.Consumed, await context.Set<StockReservation>().Where(r => r.OrderId == orderId)
                .Select(r => r.Status).SingleAsync());
            Assert.Equal(0, await context.Set<StockBalance>().Where(s => s.SkuId == sku.Id).Select(s => s.OnHand).SingleAsync());
            Assert.Equal(4, await context.Set<AuditEntry>().CountAsync(row => row.SellerId == Seller.FirstPartyId && row.Target == "LEGACY-ORDER"));
        }
        finally { await context.Database.EnsureDeletedAsync(); }
    }

    private static async Task<ShopActor> User(ShopDbContext context, string name, bool admin = false, bool staff = false)
    {
        var email = $"{name}@example.test";
        var user = new ShopUser
        {
            Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, TwoFactorEnabled = true
        };
        context.Add(user);
        await context.SaveChangesAsync();
        return new ShopActor(user.Id, email, true, staff, admin) { Mfa = true };
    }

    private static async Task<string> InviteToken(ShopDbContext context, string email)
    {
        var body = await context.Set<OutboxMessage>().Where(m => m.Recipient == email)
            .OrderByDescending(m => m.Id).Select(m => m.Body).FirstAsync();
        var link = Assert.Single(body.Split('\n', StringSplitOptions.TrimEntries), line => line.StartsWith(Origin, StringComparison.Ordinal));
        Assert.StartsWith($"{Origin}/seller/invitations/accept?token=", link);
        return Uri.UnescapeDataString(new Uri(link).Query["?token=".Length..]);
    }

    private static Order PaidOrder(ShopActor buyer, long sellerId, string number, decimal amount, DateTimeOffset now) => new()
    {
        SellerId = sellerId, Number = number, CustomerId = buyer.UserId, CustomerEmail = buyer.Email,
        SubmissionKey = Guid.NewGuid(), CreatedAt = now, Status = OrderStatus.Delivered, TotalVnd = amount,
        Payment = new Payment { Method = PaymentMethod.Cod, Status = PaymentStatus.Paid, AmountVnd = amount, PaidAt = now }
    };

    private static Refund CompletedRefund(Order order, decimal amount, DateTimeOffset now) => new()
    {
        OrderId = order.Id, AmountVnd = amount, BusinessKey = order.Number, Status = RefundStatus.Completed,
        RequestedAt = now, CompletedAt = now
    };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
