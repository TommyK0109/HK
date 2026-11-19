using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Kafka;
using HKShop.Notification;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class NotificationDeliveryFactAttribute : SqlFactAttribute
{
    public NotificationDeliveryFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")) ||
            Environment.GetEnvironmentVariable("HKSHOP_TEST_MAILPIT") != "1")
            Skip = "Requires a disposable Kafka broker and local Mailpit on ports 1025/8025 (HKSHOP_TEST_MAILPIT=1).";
    }
}

public sealed partial class SqlMarketplaceCheckoutTests
{
    [NotificationDeliveryFact]
    public async Task ConfirmWhileNotificationIsOffline_ReplayCreatesOneDeliveredEmail()
    {
        var factory = SqlTestDatabase.Create();
        await using var shop = factory.CreateDbContext();
        var topic = $"hkshop.test.confirmations.{Guid.NewGuid():N}";
        var brokers = Environment.GetEnvironmentVariable("HKSHOP_TEST_KAFKA")!;
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HKSHOP_TEST_SQL"))
        {
            InitialCatalog = $"HKShopTests_Notification_{Guid.NewGuid():N}"
        };
        await using var notification = new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseSqlServer(connection.ConnectionString).Options);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = brokers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        using var relay = new OrderConfirmationRelay(new KafkaCheckoutSettings { BootstrapServers = brokers, OrderingTopic = topic },
            factory, NullLogger<OrderConfirmationRelay>.Instance);
        Process? worker = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        try
        {
            await shop.Database.MigrateAsync(token);
            var fixture = await SeedMarketplaceAsync(factory, shop);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            var quote = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, "");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, quote.Quote);
            var order = await shop.Set<Order>().SingleAsync(o => o.Id == orderId, token);
            Assert.Equal(OrderStatus.Confirmed, order.Status);
            Assert.False(await shop.Set<OutboxMessage>().AnyAsync(e => e.EventKey.EndsWith(":confirmed"), token));

            await relay.StartAsync(token);
            var confirmations = shop.Set<IntegrationOutboxEvent>().Where(e => e.EventType == "OrderConfirmed");
            while (!await confirmations.AnyAsync(e => e.PublishedAt != null, token))
                await Task.Delay(200, token);
            var partition = new TopicPartition(topic, new Partition(0));
            using var observer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = brokers, GroupId = topic, EnableAutoCommit = false
            }).Build();
            observer.Assign(new TopicPartitionOffset(partition, Offset.Beginning));
            var original = observer.Consume(token);
            Assert.Equal(orderId.ToString("D"), original.Message.Key);
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = brokers }).Build();
            var replay = await producer.ProduceAsync(topic, original.Message, token);

            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(NotificationDbContext).Assembly.Location);
            start.Environment["ConnectionStrings__Notification"] = connection.ConnectionString;
            start.Environment["Kafka__BootstrapServers"] = brokers;
            start.Environment["Kafka__OrderingTopic"] = topic;
            start.Environment["Kafka__NotificationGroupId"] = topic;
            start.Environment["Smtp__Host"] = "127.0.0.1";
            start.Environment["Smtp__Port"] = "1025";
            worker = Process.Start(start)!;
            while (observer.Committed([partition], TimeSpan.FromSeconds(5)).Single().Offset.Value <= replay.Offset.Value)
            {
                Assert.False(worker.HasExited, "Notification exited before committing the replay.");
                await Task.Delay(250, token);
            }
            while (!await notification.ConfirmationEmails.AnyAsync(e => e.SentAt != null, token))
                await Task.Delay(250, token);
            var job = await notification.ConfirmationEmails.SingleAsync(token);
            Assert.Equal(orderId, job.OrderId);
            Assert.Equal(1, job.Attempts);
            using var http = new HttpClient();
            var query = Uri.EscapeDataString($"to:{order.CustomerEmail} subject:\"Order confirmed\"");
            using var result = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:8025/api/v1/search?query={query}", token));
            Assert.Equal(1, result.RootElement.GetProperty("messages").GetArrayLength());
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                worker.Dispose();
            }
            await relay.StopAsync(CancellationToken.None);
            await admin.DeleteTopicsAsync([topic]);
            await notification.Database.EnsureDeletedAsync();
            await shop.Database.EnsureDeletedAsync();
        }
    }
}
