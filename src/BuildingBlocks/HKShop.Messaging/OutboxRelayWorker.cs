using System.Text.Json;
using Confluent.Kafka;
using HKShop.Contracts.Events;
using HKShop.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Messaging;

public sealed record OutboxRelayOptions(string BootstrapServers, string Topic, string? ProducerName = null);

public sealed class OutboxRelayWorker<TDbContext>(
    IDbContextFactory<TDbContext> factory,
    OutboxRelayOptions options,
    ILogger<OutboxRelayWorker<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapServers) || string.IsNullOrWhiteSpace(options.Topic))
            return;

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
            MessageTimeoutMs = 5000
        }).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishBatchAsync(producer, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Outbox relay for {Topic} will retry after {ErrorType}.", options.Topic, error.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public async Task<int> PublishBatchAsync(IProducer<string, string> producer, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var outbox = db.Set<ServiceOutboxEvent>();
        var batch = await outbox.AsNoTracking()
            .Where(e => e.PublishedAt == null)
            .OrderBy(e => e.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var entry in batch)
        {
            using var payload = JsonDocument.Parse(entry.PayloadJson);
            var envelope = new EventEnvelope(
                entry.EventId,
                entry.EventType,
                1,
                entry.AggregateType,
                entry.AggregateId,
                entry.OccurredAt,
                entry.AggregateId,
                null,
                payload.RootElement);

            var headers = new Headers();
            using var activity = KafkaTelemetry.StartProducerActivity(
                $"{options.ProducerName ?? typeof(TDbContext).Name}.Publish",
                headers,
                options.Topic,
                entry.AggregateId);

            var receipt = await producer.ProduceAsync(options.Topic, new Message<string, string>
            {
                Key = entry.AggregateId,
                Value = JsonSerializer.Serialize(envelope),
                Headers = headers
            }, cancellationToken);

            if (receipt.Status != PersistenceStatus.Persisted)
                throw new InvalidOperationException($"Kafka did not acknowledge outbox event {entry.EventId:D}.");

            await outbox.Where(row => row.Id == entry.Id && row.PublishedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.PublishedAt, DateTimeOffset.UtcNow), cancellationToken);
            published++;
        }

        return published;
    }
}
