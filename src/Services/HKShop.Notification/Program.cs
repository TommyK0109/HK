using HKShop.Contracts.Events;
using HKShop.Infrastructure.Email;
using HKShop.Messaging;
using HKShop.Notification;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddHKShopDefaults("HKShop.Notification");
var connection = builder.Configuration.GetConnectionString("Notification")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Notification for the Notification database.");
builder.Services.AddDbContextFactory<NotificationDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddHealthChecks()
    .AddHKShopDbReadiness<NotificationDbContext>("sql")
    .AddHKShopKafkaReadiness("kafka");
builder.Services.AddSingleton(builder.Configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new());
builder.Services.AddSingleton<SmtpSender>();
builder.Services.AddSingleton(new NotificationKafka(
    builder.Configuration["Kafka:BootstrapServers"] ?? "127.0.0.1:9092",
    builder.Configuration["Kafka:OrderingTopic"] ?? OrderConfirmed.Topic,
    builder.Configuration["Kafka:NotificationGroupId"] ?? "hkshop.notification.confirmations.v1"));
builder.Services.AddHostedService<ConfirmationConsumer>();
builder.Services.AddHostedService<ConfirmationEmailSender>();
builder.Services.AddHostedService<NotificationMessageSender>();

var eventTopics = (builder.Configuration["Kafka:EventTopics"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (eventTopics.Length > 0)
{
    builder.Services.AddScoped<IIntegrationEventHandler<NotificationDbContext>, NotificationInboxHandler>();
    builder.Services.AddSingleton(new InboxConsumerOptions(
        builder.Configuration["Kafka:BootstrapServers"] ?? "127.0.0.1:9092",
        builder.Configuration["Kafka:NotificationEventsGroupId"] ?? "hkshop.notification.events.v1",
        "notification",
        eventTopics));
    builder.Services.AddHostedService<InboxConsumerWorker<NotificationDbContext>>();
}

using var host = builder.Build();
await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NotificationDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
await host.RunAsync();
