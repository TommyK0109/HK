using System.Text.Json;
using Confluent.Kafka;
using HKShop.Contracts.Events;
using HKShop.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Notification;

public sealed record NotificationKafka(string Brokers, string Topic, string GroupId);

public sealed class ConfirmationConsumer(NotificationKafka settings, IDbContextFactory<NotificationDbContext> database,
    ILogger<ConfirmationConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var deadLetters = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = settings.Brokers, EnableIdempotence = true, AllowAutoCreateTopics = false,
            MessageTimeoutMs = 5000
        }).Build();
        var config = new ConsumerConfig
        {
            BootstrapServers = settings.Brokers, GroupId = settings.GroupId, EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest, AllowAutoCreateTopics = false
        };
        while (!token.IsCancellationRequested)
        {
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            try
            {
                consumer.Subscribe(settings.Topic);
                while (!token.IsCancellationRequested)
                {
                    var record = consumer.Consume(token);
                    using var activity = KafkaTelemetry.StartConsumerActivity(
                        record.Message.Headers, "ConfirmationConsumer.Process", record.Topic, record.Message.Key);
                    try
                    {
                        await Enqueue(record.Message.Value, token);
                    }
                    catch (JsonException)
                    {
                        var result = await deadLetters.ProduceAsync(settings.Topic + ".notification.dlq.v1",
                            record.Message, token);
                        if (result.Status != PersistenceStatus.Persisted)
                            throw new InvalidOperationException("Kafka did not acknowledge the dead letter.");
                        logger.LogWarning("Invalid event at {Offset} sent to the Notification dead-letter topic.",
                            record.TopicPartitionOffset);
                    }
                    consumer.Commit(record);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Notification will retry from the committed offset after {ErrorType}.", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            finally { consumer.Close(); }
        }
    }

    private async Task Enqueue(string? value, CancellationToken token)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(value ?? "null");
        if (string.IsNullOrEmpty(envelope?.Type)) throw new JsonException("Missing event type.");
        if (envelope.Type != nameof(OrderConfirmed)) return;
        if (envelope.Version != 1 || envelope.EventId == Guid.Empty || envelope.AggregateType != "Order" ||
            !Guid.TryParse(envelope.AggregateId, out var orderId) || orderId == Guid.Empty ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Invalid order confirmation envelope.");
        var order = envelope.Payload.Deserialize<OrderConfirmed>();
        if (string.IsNullOrWhiteSpace(order?.OrderNumber) || string.IsNullOrWhiteSpace(order.Recipient))
            throw new JsonException("Invalid order confirmation payload.");

        await using var db = await database.CreateDbContextAsync(token);
        db.ConfirmationEmails.Add(new ConfirmationEmail
        {
            OrderId = orderId, EventId = envelope.EventId, Recipient = order.Recipient,
            OrderNumber = order.OrderNumber, NextAttemptAt = DateTimeOffset.UtcNow
        });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2627 })
        {
            // The job itself is the inbox receipt. The order ID primary key also covers producer retries.
            logger.LogDebug("Confirmation already recorded for order {OrderId}.", orderId);
        }
    }
}
