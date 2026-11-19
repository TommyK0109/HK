using System.Text.Json;
using Confluent.Kafka;
using HKShop.Contracts.Events;
using HKShop.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Messaging;

public sealed record InboxConsumerOptions(
    string BootstrapServers,
    string GroupId,
    string ConsumerName,
    IReadOnlyList<string> Topics,
    int MaxAttempts = 5,
    int InitialBackoffMs = 200);

public interface IIntegrationEventHandler<in TDbContext> where TDbContext : DbContext
{
    Task HandleAsync(TDbContext db, EventEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class InboxConsumerWorker<TDbContext>(
    InboxConsumerOptions options,
    IServiceScopeFactory scopes,
    ILogger<InboxConsumerWorker<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapServers) || options.Topics.Count == 0)
            return;

        using var deadLetters = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
            MessageTimeoutMs = 5000
        }).Build();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = false
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
            try
            {
                consumer.Subscribe(options.Topics);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);
                    using var activity = KafkaTelemetry.StartConsumerActivity(
                        record.Message.Headers,
                        $"{options.ConsumerName}.Consume",
                        record.Topic,
                        record.Message.Key);

                    await ProcessWithRetryAndDlqAsync(record, deadLetters, stoppingToken);
                    consumer.Commit(record);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Inbox consumer {Consumer} will reconnect from committed offset after {ErrorType}.",
                    options.ConsumerName, error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            finally
            {
                consumer.Close();
            }
        }
    }

    private async Task ProcessWithRetryAndDlqAsync(
        ConsumeResult<string, string> record,
        IProducer<string, string> deadLetters,
        CancellationToken cancellationToken)
    {
        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(record.Message.Value ?? "null");
            if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Type))
                throw new JsonException("Invalid integration event envelope.");
        }
        catch (JsonException ex)
        {
            await SendToDlqAsync(deadLetters, record, ex, cancellationToken);
            return;
        }

        var maxAttempts = Math.Max(1, options.MaxAttempts);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DispatchInTransactionAsync(envelope, cancellationToken);
                return;
            }
            catch (JsonException ex)
            {
                await SendToDlqAsync(deadLetters, record, ex, cancellationToken);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = ex;
                logger.LogWarning(ex,
                    "Inbox consumer {Consumer} attempt {Attempt}/{MaxAttempts} failed for {EventType} ({EventId}).",
                    options.ConsumerName, attempt, maxAttempts, envelope.Type, envelope.EventId);
                if (attempt < maxAttempts)
                {
                    var delayMs = options.InitialBackoffMs * (1 << (attempt - 1));
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delayMs, 5000)), cancellationToken);
                }
            }
        }

        await SendToDlqAsync(deadLetters, record, lastFailure!, cancellationToken);
    }

    private async Task DispatchInTransactionAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
        var handler = scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<TDbContext>>();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var eventKey = $"{envelope.EventId:D}";
        var alreadyConsumed = await db.Set<ServiceConsumedEvent>()
            .AnyAsync(c => c.ConsumerName == options.ConsumerName && c.EventKey == eventKey, cancellationToken);
        if (alreadyConsumed)
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        db.Set<ServiceConsumedEvent>().Add(new ServiceConsumedEvent
        {
            ConsumerName = options.ConsumerName,
            EventKey = eventKey,
            ConsumedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await tx.RollbackAsync(cancellationToken);
            return;
        }

        await handler.HandleAsync(db, envelope, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task SendToDlqAsync(
        IProducer<string, string> deadLetters,
        ConsumeResult<string, string> record,
        Exception reason,
        CancellationToken cancellationToken)
    {
        var dlqTopic = KafkaTopics.Dlq(record.Topic, options.ConsumerName);
        var headers = new Headers();
        if (record.Message.Headers is not null)
        {
            foreach (var h in record.Message.Headers)
                headers.Add(h.Key, h.GetValueBytes());
        }
        headers.Add("x-dlq-reason", System.Text.Encoding.UTF8.GetBytes(reason.Message));

        var result = await deadLetters.ProduceAsync(dlqTopic, new Message<string, string>
        {
            Key = record.Message.Key,
            Value = record.Message.Value,
            Headers = headers
        }, cancellationToken);

        if (result.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException($"Kafka did not acknowledge DLQ message on {dlqTopic}.");

        logger.LogError(reason, "Sent poison message at {TopicPartitionOffset} to DLQ {DlqTopic}.",
            record.TopicPartitionOffset, dlqTopic);
    }
}
