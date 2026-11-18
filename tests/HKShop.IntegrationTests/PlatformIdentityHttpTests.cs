using System.Net;
using HKShop.Domain.Cart;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class PlatformIdentityHttpTests
{
    private const string Password = "Platform fixture password!";

    [SqlFact]
    public async Task LoginFormsRejectWrongPlatformAndSendEligibleUsersToTheirPlatform()
    {
        await using var db = SqlTestDatabase.Create().CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
            using var app = PlatformHttpTestHost.Create(db.Database.GetConnectionString());
            var accounts = await SeedAccountsAsync(app, db);

            foreach (var employee in new[] { accounts.Admin, accounts.Staff, accounts.Shipper })
            {
                using var wrongBrowser = app.CreateBrowser();
                using var wrongLogin = await LoginAsync(wrongBrowser, "/account/login", employee);
                AssertRedirect(wrongLogin, "/account/login");
                Assert.Contains("Employee and shipper accounts must sign in through EOffice",
                    await wrongBrowser.GetStringAsync("/account/login"));
                using var orders = await wrongBrowser.GetAsync("/orders");
                AssertRedirect(orders, "/account/login", "/orders");

                using var rightBrowser = app.CreateBrowser();
                using var rightLogin = await LoginAsync(rightBrowser, "/eoffice/login", employee);
                AssertRedirect(rightLogin, "/eoffice/mfa", employee.Id == accounts.Shipper.Id ? "/eoffice/shipper" : "/eoffice");
            }

            foreach (var shopper in new[] { accounts.Customer, accounts.Seller })
            {
                using var wrongBrowser = app.CreateBrowser();
                using var wrongLogin = await LoginAsync(wrongBrowser, "/eoffice/login", shopper);
                AssertRedirect(wrongLogin, "/eoffice/login");
                Assert.Contains("Only HKShop staff, administrators, and shippers can sign in to EOffice",
                    await wrongBrowser.GetStringAsync("/eoffice/login"));
                using var inventory = await wrongBrowser.GetAsync("/eoffice/inventory");
                AssertRedirect(inventory, "/eoffice/login", "/eoffice/inventory");

                using var rightBrowser = app.CreateBrowser();
                using var rightLogin = await LoginAsync(rightBrowser, "/account/login", shopper);
                if (shopper.Id == accounts.Seller.Id)
                    AssertRedirect(rightLogin, "/account/mfa", "/");
                else
                {
                    AssertRedirect(rightLogin, "/");
                    using var orders = await rightBrowser.GetAsync("/orders");
                    Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
                }
            }
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task CommerceAndEOfficeActionsEnforceAccountRolesAndMfa()
    {
        await using var db = SqlTestDatabase.Create().CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
            using var app = PlatformHttpTestHost.Create(db.Database.GetConnectionString(), enableTransport: true);
            var accounts = await SeedAccountsAsync(app, db);
            const string eofficeForm = "/eoffice/login";

            foreach (var employee in new[] { accounts.Admin, accounts.Staff, accounts.Shipper })
            {
                using var client = await app.CreateBrowserAsync(employee.Id);
                foreach (var path in new[]
                {
                    "/api/commerce/checkout/place", "/api/commerce/reviews/isolation-fixture",
                    "/api/commerce/seller/register", "/api/commerce/inventory/seller/2/offers/1/stock"
                })
                {
                    using var response = await client.PostAsync(path, content: null);
                    AssertRedirect(response, "/account/denied", path);
                }

                using var cartResponse = await PlatformHttpTestHost.PostFormAsync(client, "/api/commerce/cart", eofficeForm,
                    new() { ["offerId"] = "1", ["quantity"] = "1", ["add"] = "true" });
                AssertRedirect(cartResponse, eofficeForm);
                Assert.Contains("Employee and shipper accounts cannot use shopping features",
                    await client.GetStringAsync(eofficeForm));
                Assert.False(await db.Set<ShoppingCart>().AnyAsync(cart => cart.CustomerId == employee.Id));
            }

            foreach (var shopper in new[] { accounts.Customer, accounts.Seller })
            {
                using var client = await app.CreateBrowserAsync(shopper.Id, mfa: shopper.TwoFactorEnabled);
                await AssertDeniedAsync(client, HttpMethod.Get, "/eoffice/inventory");
                await AssertDeniedAsync(client, HttpMethod.Get, "/eoffice/shipper");
                await AssertDeniedAsync(client, HttpMethod.Post, "/api/eoffice/inventory/stock");
                await AssertDeniedAsync(client, HttpMethod.Post, "/api/eoffice/shipper/presence");
            }

            using var staffWithoutMfa = await app.CreateBrowserAsync(accounts.Staff.Id, mfa: false);
            await AssertDeniedAsync(staffWithoutMfa, HttpMethod.Get, "/eoffice/inventory");
            await AssertDeniedAsync(staffWithoutMfa, HttpMethod.Post, "/api/eoffice/inventory/stock");

            using var shipperWithoutMfa = await app.CreateBrowserAsync(accounts.Shipper.Id, mfa: false);
            await AssertDeniedAsync(shipperWithoutMfa, HttpMethod.Get, "/eoffice/shipper");
            await AssertDeniedAsync(shipperWithoutMfa, HttpMethod.Get, "/api/eoffice/shipper/state");

            using var staff = await app.CreateBrowserAsync(accounts.Staff.Id);
            var inventory = await staff.GetStringAsync("/eoffice/inventory");
            Assert.Contains("href=\"/eoffice/transport\"", inventory);
            Assert.DoesNotContain("href=\"/eoffice/users\"", inventory);
            await AssertDeniedAsync(staff, HttpMethod.Post, "/api/eoffice/transport/shippers");
            await AssertDeniedAsync(staff, HttpMethod.Get, "/eoffice/users");
            await AssertDeniedAsync(staff, HttpMethod.Get, "/api/eoffice/shipper/state");

            using var shipper = await app.CreateBrowserAsync(accounts.Shipper.Id);
            var workspace = await shipper.GetStringAsync("/eoffice/shipper");
            Assert.Contains("href=\"/eoffice/shipper\"", workspace);
            Assert.DoesNotContain("href=\"/eoffice/products\"", workspace);
            Assert.DoesNotContain("aria-label=\"Shopping cart\"", workspace);
            using var state = await shipper.GetAsync("/api/eoffice/shipper/state");
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            await AssertDeniedAsync(shipper, HttpMethod.Get, "/eoffice/products");
            await AssertDeniedAsync(shipper, HttpMethod.Get, "/eoffice/users");

            using var admin = await app.CreateBrowserAsync(accounts.Admin.Id);
            using var adminUsers = await admin.GetAsync("/eoffice/users");
            Assert.Equal(HttpStatusCode.OK, adminUsers.StatusCode);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [SqlFact]
    public async Task ShipperEnrollmentRejectsAccountsWithActiveSellerMembershipsOrCustomerOrders()
    {
        await using var db = SqlTestDatabase.Create().CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
            using var app = PlatformHttpTestHost.Create(db.Database.GetConnectionString(), enableTransport: true);
            var accounts = await SeedAccountsAsync(app, db);
            using var scope = app.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ShopUser>>();
            var eligible = await CreateUserAsync(users, "eligible", mfa: false);
            db.Add(new Order
            {
                CustomerId = accounts.Customer.Id, CustomerEmail = accounts.Customer.Email!,
                Number = "TEST-01", SubmissionKey = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
                Status = OrderStatus.Confirmed, Recipient = "Test Customer", Phone = "0900000000",
                ShippingAddress = "1 Test Road", TotalVnd = 100_000
            });
            await db.SaveChangesAsync();

            using var admin = await app.CreateBrowserAsync(accounts.Admin.Id);
            await EnrollAsync(admin, eligible);
            Assert.True(await users.IsInRoleAsync(eligible, "Shipper"));
            Assert.True(await db.Set<TransportShipper>().AnyAsync(row => row.UserId == eligible.Id && row.Active));

            var actual = new List<(string Email, bool Role, bool ShippingAccess)>();
            foreach (var account in new[] { accounts.Seller, accounts.Customer })
            {
                await EnrollAsync(admin, account);
                actual.Add((account.Email!, await users.IsInRoleAsync(account, "Shipper"),
                    await db.Set<TransportShipper>().AnyAsync(row => row.UserId == account.Id)));
            }
            Assert.Equal(
                new[] { (accounts.Seller.Email!, false, false), (accounts.Customer.Email!, false, false) },
                actual);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string loginPath, ShopUser user) =>
        PlatformHttpTestHost.PostFormAsync(browser,
            loginPath == "/account/login" ? "/api/commerce/account/login" : "/api/eoffice/account/login", loginPath,
            new() { ["email"] = user.Email!, ["password"] = Password });

    private static async Task EnrollAsync(HttpClient browser, ShopUser user)
    {
        using var response = await PlatformHttpTestHost.PostFormAsync(browser, "/api/eoffice/transport/shippers",
            "/eoffice/transport", new() { ["email"] = user.Email!, ["active"] = "true" });
        AssertRedirect(response, "/eoffice/transport");
    }

    private static async Task AssertDeniedAsync(HttpClient client, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        using var response = await client.SendAsync(request);
        AssertRedirect(response, "/eoffice/denied", path);
    }

    private static void AssertRedirect(HttpResponseMessage response, string path, string? returnUrl = null)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var location = new Uri(response.RequestMessage!.RequestUri!, response.Headers.Location);
        Assert.Equal(path, location.AbsolutePath);
        if (returnUrl is not null)
            Assert.Equal(returnUrl, QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString());
    }

    private static async Task<(ShopUser Customer, ShopUser Seller, ShopUser Admin, ShopUser Staff, ShopUser Shipper)>
        SeedAccountsAsync(PlatformHttpTestHost app, ShopDbContext db)
    {
        using var scope = app.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "StoreStaff", "Shipper" })
            AssertSucceeded(await roles.CreateAsync(new IdentityRole(role)));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ShopUser>>();
        var customer = await CreateUserAsync(users, "customer", mfa: false);
        var seller = await CreateUserAsync(users, "seller");
        var admin = await CreateUserAsync(users, "admin", "Admin");
        var staff = await CreateUserAsync(users, "staff", "StoreStaff");
        var shipper = await CreateUserAsync(users, "shipper", "Shipper");

        var shop = new Seller { Slug = "boundary-seller", DisplayName = "Boundary Seller", Status = SellerStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow };
        db.Add(shop);
        await db.SaveChangesAsync();
        db.Add(new SellerMembership { SellerId = shop.Id, UserId = seller.Id, Email = seller.Email!,
            Role = SellerRole.Owner, Status = SellerMembershipStatus.Active });
        db.Add(new TransportShipper { UserId = shipper.Id, Email = shipper.Email!, Active = true });
        await db.SaveChangesAsync();
        return (customer, seller, admin, staff, shipper);
    }

    private static async Task<ShopUser> CreateUserAsync(UserManager<ShopUser> users, string name, string? role = null,
        bool mfa = true)
    {
        var email = name + "@example.test";
        var user = new ShopUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name };
        AssertSucceeded(await users.CreateAsync(user, Password));
        if (role is not null) AssertSucceeded(await users.AddToRoleAsync(user, role));
        if (mfa)
        {
            AssertSucceeded(await users.ResetAuthenticatorKeyAsync(user));
            AssertSucceeded(await users.SetTwoFactorEnabledAsync(user, true));
        }
        return user;
    }

    private static void AssertSucceeded(IdentityResult result) =>
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
}
