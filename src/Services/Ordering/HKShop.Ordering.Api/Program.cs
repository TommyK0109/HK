using HKShop.Application;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Messaging;
using HKShop.Ordering.Api.Api;
using HKShop.Ordering.Api.Application;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Ordering");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration.GetSection("Shop").Get<ShopSettings>() ?? new());
builder.Services.AddSingleton(new ShippingClientOptions
{
    Enabled = builder.Configuration.GetValue("Transport:Enabled", false)
});

var connection = builder.Configuration.GetConnectionString("Ordering")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Ordering before starting the Ordering service.");
builder.Services.AddDbContextFactory<OrderingDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<OrderingDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");

builder.Services.AddHttpClient<CatalogServiceClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Catalog:BaseUrl"] ?? "http://localhost:5102"))
    .AddHKShopResilienceHandler();
builder.Services.AddHttpClient<ShippingServiceClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Shipping:BaseUrl"] ?? "http://localhost:5105"))
    .AddHKShopResilienceHandler();
builder.Services.AddHttpClient<IdentityServiceClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Identity:BaseUrl"] ?? "http://localhost:5101"))
    .AddHKShopResilienceHandler();

builder.Services.AddScoped<OrderingSagaOrchestrator>();
builder.Services.AddScoped<OrderingCart>();
builder.Services.AddHKShopMessaging<OrderingDbContext, OrderingInboxHandler>(
    builder.Configuration,
    producerTopic: KafkaTopics.Ordering,
    consumerName: "ordering",
    consumerGroupId: builder.Configuration["Kafka:OrderingGroupId"] ?? "hkshop.ordering.v1",
    KafkaTopics.Catalog, KafkaTopics.Payments, KafkaTopics.Shipping);

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<OrderingDbContext>>().CreateDbContextAsync())
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
app.MapOrderingEndpoints();
app.Run();
