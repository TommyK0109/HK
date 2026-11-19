using System.Text.Json;
using Confluent.Kafka;
using HKShop.Contracts.Events;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Persistence;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Infrastructure.Kafka;

public sealed class OrderConfirmationRelay(KafkaCheckoutSettings settings, IDbContextFactory<ShopDbContext> factory,
    ILogger<OrderConfirmationRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers, EnableIdempotence = true,
            AllowAutoCreateTopics = false, MessageTimeoutMs = 5000
        }).Build();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(token);
                var outbox = db.Set<IntegrationOutboxEvent>();
                var batch = await outbox.AsNoTracking()
                    .Where(e => e.EventType == nameof(OrderConfirmed) && e.PublishedAt == null)
                    .OrderBy(e => e.Id).Take(50).ToListAsync(token);
                foreach (var e in batch)
                {
                    using var payload = JsonDocument.Parse(e.PayloadJson);
                    var envelope = new EventEnvelope(e.EventId, e.EventType, 1, e.AggregateType, e.AggregateId,
                        e.OccurredAt, e.AggregateId, null, payload.RootElement);
                    var headers = new Headers();
                    using var activity = KafkaTelemetry.StartProducerActivity(
                        "OrderConfirmationRelay.Publish", headers, settings.OrderingTopic, e.AggregateId);
                    var receipt = await producer.ProduceAsync(settings.OrderingTopic, new Message<string, string>
                    {
                        Key = e.AggregateId, Value = JsonSerializer.Serialize(envelope), Headers = headers
                    }, token);
                    if (receipt.Status != PersistenceStatus.Persisted)
                        throw new InvalidOperationException("Kafka did not acknowledge the confirmation.");
                    await outbox.Where(row => row.Id == e.Id && row.PublishedAt == null)
                        .ExecuteUpdateAsync(update => update.SetProperty(row => row.PublishedAt, DateTimeOffset.UtcNow), token);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Order confirmation relay will retry after {ErrorType}.", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
