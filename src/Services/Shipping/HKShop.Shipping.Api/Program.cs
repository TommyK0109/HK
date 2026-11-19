using HKShop.Application.Transportation;
using HKShop.Contracts.Events;
using HKShop.Messaging;
using HKShop.ServiceDefaults;
using HKShop.Shipping.Api.Api;
using HKShop.Shipping.Api.Application;
using HKShop.Shipping.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Shipping");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration.GetSection("Transport").Get<TransportSettings>() ?? new());

var connection = builder.Configuration.GetConnectionString("Shipping")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Shipping before starting the Shipping service.");
builder.Services.AddDbContextFactory<ShippingDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<ShippingDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");

builder.Services.AddHttpClient<ITransportRouting, ShippingOsrmRouting>(client => client.Timeout = TimeSpan.FromSeconds(4));
builder.Services.AddScoped<ShippingOrderLifecycle>();
builder.Services.AddHostedService<ShippingDispatchWorker>();
builder.Services.AddHKShopMessaging<ShippingDbContext, ShippingInboxHandler>(
    builder.Configuration,
    producerTopic: KafkaTopics.Shipping,
    consumerName: "shipping",
    consumerGroupId: builder.Configuration["Kafka:ShippingGroupId"] ?? "hkshop.shipping.v1",
    KafkaTopics.Ordering, KafkaTopics.Payments, KafkaTopics.Catalog, KafkaTopics.Identity);

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ShippingDbContext>>().CreateDbContextAsync())
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
app.MapShippingEndpoints();
app.Run();
