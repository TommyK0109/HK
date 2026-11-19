using HKShop.Application;
using HKShop.Contracts.Events;
using HKShop.Messaging;
using HKShop.Payments.Api.Api;
using HKShop.Payments.Api.Application;
using HKShop.Payments.Api.Infrastructure;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Payments");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration.GetSection("Shop").Get<ShopSettings>() ?? new());
builder.Services.AddSingleton(builder.Configuration.GetSection("SePay").Get<SePayOptions>() ?? new());
builder.Services.AddSingleton(builder.Configuration.GetSection("PayoutSimulation").Get<PayoutSimulationOptions>() ?? new());

var connection = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Payments before starting the Payments service.");
builder.Services.AddDbContextFactory<PaymentsDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<PaymentsDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");

builder.Services.AddHttpClient<SePayPaymentGateway>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<SePayRefundGatewayService>();
builder.Services.AddSingleton<SimulatedPayoutGatewayService>();
builder.Services.AddScoped<PaymentsProcessor>();
builder.Services.AddHostedService<PaymentsMaintenanceWorker>();
builder.Services.AddHKShopMessaging<PaymentsDbContext, PaymentsInboxHandler>(
    builder.Configuration,
    producerTopic: KafkaTopics.Payments,
    consumerName: "payments",
    consumerGroupId: builder.Configuration["Kafka:PaymentsGroupId"] ?? "hkshop.payments.v1",
    KafkaTopics.Ordering, KafkaTopics.Shipping, KafkaTopics.Catalog);

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<PaymentsDbContext>>().CreateDbContextAsync())
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
app.MapPaymentsEndpoints();
app.Run();
