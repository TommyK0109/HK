using HKShop.Application;
using HKShop.Contracts.Events;
using HKShop.Identity.Api.Api;
using HKShop.Identity.Api.Application;
using HKShop.Identity.Api.Domain;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Messaging;
using HKShop.ServiceDefaults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Identity");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration.GetSection("Shop").Get<ShopSettings>() ?? new());

var connection = builder.Configuration.GetConnectionString("Identity")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Identity before starting the Identity service.");
builder.Services.AddDbContextFactory<IdentityServiceDbContext>(options => options.UseSqlServer(connection));

builder.Services.AddIdentityCore<ShopUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireDigit = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<IdentityServiceDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<IdentityServiceDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");

builder.Services.AddScoped<JwtTokenIssuer>();
builder.Services.AddSingleton(new OutboxRelayOptions(
    builder.Configuration["Kafka:BootstrapServers"] ?? "",
    KafkaTopics.Identity,
    "identity"));
builder.Services.AddHostedService<OutboxRelayWorker<IdentityServiceDbContext>>();

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<IdentityServiceDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "StoreStaff", "Admin", "Shipper" })
        if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));

    var email = builder.Configuration["Bootstrap:AdminEmail"]?.Trim();
    var password = builder.Configuration["Bootstrap:AdminPassword"];
    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ShopUser>>();
        if (await users.FindByEmailAsync(email) is null)
        {
            var admin = new ShopUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
            var res = await users.CreateAsync(admin, password);
            if (res.Succeeded) await users.AddToRoleAsync(admin, "Admin");
        }
    }
    return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseHKShopSecurityHeaders();
app.UseHKShopRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapHKShopHealthChecks();
app.MapIdentityEndpoints();
app.Run();
