using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Catalog.Api.Api;
using HKShop.Catalog.Api.Application;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Events;
using HKShop.Messaging;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Catalog");
builder.Services.AddSingleton(TimeProvider.System);

var shop = builder.Configuration.GetSection("Shop").Get<ShopSettings>() ?? new();
shop.MediaPath = Path.GetFullPath(shop.MediaPath, builder.Environment.ContentRootPath);
shop.KeysPath = Path.GetFullPath(shop.KeysPath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(shop.MediaPath);
Directory.CreateDirectory(shop.KeysPath);
builder.Services.AddDataProtection().SetApplicationName("HKShop")
    .PersistKeysToFileSystem(new DirectoryInfo(shop.KeysPath));
builder.Services.AddSingleton(shop);

var s3Media = builder.Configuration.GetSection("S3").Get<CatalogS3MediaSettings>() ?? new();
builder.Services.AddSingleton(s3Media);
if (s3Media.Enabled)
    builder.Services.AddSingleton<IProductImageStore>(_ => new CatalogS3ProductImages(s3Media));
else
    builder.Services.AddSingleton<IProductImageStore, CatalogLocalProductImages>();

var connection = builder.Configuration.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Catalog before starting the Catalog service.");
builder.Services.AddDbContextFactory<CatalogDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<CatalogDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");

builder.Services.AddScoped<CatalogStockCoordinator>();
builder.Services.AddHKShopMessaging<CatalogDbContext, CatalogInboxHandler>(
    builder.Configuration,
    producerTopic: KafkaTopics.Catalog,
    consumerName: "catalog",
    consumerGroupId: builder.Configuration["Kafka:CatalogGroupId"] ?? "hkshop.catalog.v1",
    KafkaTopics.Ordering);

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseHKShopSecurityHeaders();
app.UseHKShopRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapHKShopHealthChecks();
app.MapCatalogEndpoints();
app.Run();
