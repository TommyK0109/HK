using System.Diagnostics;
using Confluent.Kafka;
using HKShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Infrastructure.Kafka;

public sealed class KafkaCheckoutSettings
{
    public string BootstrapServers { get; set; } = "";
    public string CheckoutTopic { get; set; } = "hkshop.checkout.requests.v1";
    public string CheckoutGroupId { get; set; } = "hkshop.checkout.v1";
}

internal static class KafkaClientLogging
{
    public static void LogMessage(ILogger logger, string role, LogMessage message)
    {
        var level = message.Level switch
        {
            SyslogLevel.Emergency or SyslogLevel.Alert or SyslogLevel.Critical => LogLevel.Critical,
            SyslogLevel.Error => LogLevel.Error,
            SyslogLevel.Warning => LogLevel.Warning,
            SyslogLevel.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, "{LogSource} {KafkaRole} [{KafkaFacility}] ({KafkaClient}): {KafkaMessage}",
            "Kafka", role, message.Facility, message.Name, message.Message);
    }

    public static void LogError(ILogger logger, string role, Error error)
    {
        var level = error.IsFatal ? LogLevel.Error : LogLevel.Warning;
        logger.Log(level, "{LogSource} {KafkaRole} error {ErrorCode} (BrokerError={IsBrokerError}, Fatal={IsFatal}): {Reason}",
            "Kafka", role, error.Code, error.IsBrokerError, error.IsFatal, error.Reason);
    }
}

public sealed class CheckoutPublisher(KafkaCheckoutSettings settings, TimeProvider time, IServiceScopeFactory scopes,
    IDbContextFactory<ShopDbContext> factory, ILogger<CheckoutPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers, EnableIdempotence = true,
            Acks = Acks.All, MessageTimeoutMs = 5000, AllowAutoCreateTopics = false
        };
        var nextAgeCheck = DateTimeOffset.MinValue;
        var partition = new TopicPartition(settings.CheckoutTopic, new Partition(0));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var producer = new ProducerBuilder<Null, string>(config)
                    .SetLogHandler((_, message) => KafkaClientLogging.LogMessage(logger, "producer", message))
                    .SetErrorHandler((_, error) => KafkaClientLogging.LogError(logger, "producer", error))
                    .Build();
                logger.LogInformation("{LogSource} {KafkaRole} ready for {Topic} ({BootstrapServers}).",
                    "Kafka", "producer", settings.CheckoutTopic, settings.BootstrapServers);

                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var expired = await scope.ServiceProvider.GetRequiredService<CheckoutRequests>().Expire(stoppingToken);
                    if (expired > 0) logger.LogInformation("Expired {Count} pending checkout requests.", expired);

                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var pending = db.Set<CheckoutRequest>().Where(r => r.Status == CheckoutRequestStatus.Pending);
                    var now = time.GetUtcNow();
                    if (now >= nextAgeCheck)
                    {
                        nextAgeCheck = now.AddMinutes(1);
                        var oldest = await pending.Select(r => (DateTimeOffset?)r.CreatedAt).MinAsync(stoppingToken);
                        if (oldest is not null && now - oldest.Value >= TimeSpan.FromSeconds(30))
                            logger.LogWarning("Oldest pending checkout request is {AgeSeconds:F0} seconds old.",
                                (now - oldest.Value).TotalSeconds);
                    }

                    var ids = await pending.Where(r => r.PublishedAt == null && r.ExpiresAt > now)
                        .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.Id).Take(50)
                        .ToListAsync(stoppingToken);

                    foreach (var id in ids)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var receipt = await producer.ProduceAsync(partition, new Message<Null, string> { Value = id.ToString("D") },
                            stoppingToken);
                        if (receipt.Status != PersistenceStatus.Persisted)
                            throw new InvalidOperationException("Kafka did not acknowledge the checkout request.");
                        logger.LogInformation(
                            "{LogSource} {KafkaRole} published checkout request {RequestId} to {Topic}[{Partition}]@{Offset} ({Status}) in {ElapsedMs} ms.",
                            "Kafka", "producer", id, receipt.Topic, receipt.Partition.Value, receipt.Offset.Value,
                            receipt.Status, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
                        var publishedAt = time.GetUtcNow();
                        await db.Set<CheckoutRequest>().Where(r => r.Id == id && r.PublishedAt == null)
                            .ExecuteUpdateAsync(update => update.SetProperty(r => r.PublishedAt, publishedAt), stoppingToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("{LogSource} {KafkaRole} checkout publishing will retry after {Error}.",
                    "Kafka", "producer", error is KafkaException kafka ? kafka.Error.Code.ToString() : error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

public sealed class CheckoutConsumer(KafkaCheckoutSettings settings, IServiceScopeFactory scopes,
    ILogger<CheckoutConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers, GroupId = settings.CheckoutGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
            EnableAutoOffsetStore = false, AllowAutoCreateTopics = false
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var consumer = new ConsumerBuilder<Ignore, string>(config)
                    .SetLogHandler((_, message) => KafkaClientLogging.LogMessage(logger, "consumer", message))
                    .SetErrorHandler((_, error) => KafkaClientLogging.LogError(logger, "consumer", error))
                    .Build();
                consumer.Subscribe(settings.CheckoutTopic);
                logger.LogInformation("{LogSource} {KafkaRole} subscribed to {Topic} in group {GroupId} ({BootstrapServers}).",
                    "Kafka", "consumer", settings.CheckoutTopic, settings.CheckoutGroupId, settings.BootstrapServers);
                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var message = consumer.Consume(stoppingToken);
                        var started = Stopwatch.GetTimestamp();
                        if (Guid.TryParseExact(message.Message.Value, "D", out var id) && id != Guid.Empty)
                        {
                            using var scope = scopes.CreateScope();
                            await scope.ServiceProvider.GetRequiredService<CheckoutRequests>().Process(id);
                            consumer.Commit(message);
                            logger.LogInformation(
                                "{LogSource} {KafkaRole} consumed checkout request {RequestId} from {Topic}[{Partition}]@{Offset} in group {GroupId} in {ElapsedMs} ms.",
                                "Kafka", "consumer", id, message.Topic, message.Partition.Value, message.Offset.Value,
                                settings.CheckoutGroupId, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
                        }
                        else
                        {
                            logger.LogWarning(
                                "{LogSource} {KafkaRole} ignored invalid checkout message at {Topic}[{Partition}]@{Offset} in group {GroupId}.",
                                "Kafka", "consumer", message.Topic, message.Partition.Value, message.Offset.Value,
                                settings.CheckoutGroupId);
                            consumer.Commit(message);
                        }
                    }
                }
                finally { consumer.Close(); }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("{LogSource} {KafkaRole} checkout consumption will resume from the committed offset after {Error}.",
                    "Kafka", "consumer", error is KafkaException kafka ? kafka.Error.Code.ToString() : error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
