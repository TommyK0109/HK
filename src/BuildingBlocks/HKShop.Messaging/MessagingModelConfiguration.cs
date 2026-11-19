using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Messaging;

public sealed class ServiceOutboxEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string AggregateType { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

public sealed class ServiceConsumedEvent
{
    public long Id { get; set; }
    public string ConsumerName { get; set; } = "";
    public string EventKey { get; set; } = "";
    public DateTimeOffset ConsumedAt { get; set; }
}

public sealed class ServiceAuditEntry
{
    public long Id { get; set; }
    public long? SellerId { get; set; }
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

public static class MessagingModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ServiceOutboxEvent>(b =>
        {
            b.ToTable("IntegrationOutbox");
            b.Property(x => x.AggregateType).HasMaxLength(60);
            b.Property(x => x.AggregateId).HasMaxLength(80);
            b.Property(x => x.EventType).HasMaxLength(80);
            b.Property(x => x.IdempotencyKey).HasMaxLength(160);
            b.HasIndex(x => x.EventId).IsUnique();
            b.HasIndex(x => x.IdempotencyKey).IsUnique();
            b.HasIndex(x => new { x.PublishedAt, x.OccurredAt });
        });

        model.Entity<ServiceConsumedEvent>(b =>
        {
            b.ToTable("ConsumedIntegrationEvents");
            b.Property(x => x.ConsumerName).HasMaxLength(80);
            b.Property(x => x.EventKey).HasMaxLength(160);
            b.HasIndex(x => new { x.ConsumerName, x.EventKey }).IsUnique();
        });

        model.Entity<ServiceAuditEntry>(b =>
        {
            b.ToTable("AuditEntries");
            b.Property(x => x.ActorId).HasMaxLength(450);
            b.Property(x => x.Action).HasMaxLength(100);
            b.Property(x => x.Target).HasMaxLength(150);
            b.Property(x => x.Reason).HasMaxLength(250);
            b.HasIndex(x => x.OccurredAt);
            b.HasIndex(x => new { x.SellerId, x.OccurredAt });
        });
    }
}

public static class MessagingDbContextExtensions
{
    public static void EnqueueOutboxEvent(
        this DbContext db,
        string aggregateType,
        string aggregateId,
        string eventType,
        object payload,
        string idempotencyKey,
        DateTimeOffset? occurredAt = null)
    {
        var set = db.Set<ServiceOutboxEvent>();
        if (set.Local.Any(e => e.IdempotencyKey == idempotencyKey))
            return;

        set.Add(new ServiceOutboxEvent
        {
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey
        });
    }

    public static void AddAuditEntry(
        this DbContext db,
        string? actorId,
        string action,
        string target,
        DateTimeOffset occurredAt,
        string reason = "",
        long? sellerId = null)
    {
        db.Set<ServiceAuditEntry>().Add(new ServiceAuditEntry
        {
            ActorId = actorId,
            Action = action,
            Target = target,
            Reason = reason,
            OccurredAt = occurredAt,
            SellerId = sellerId
        });
    }
}

public static class SerializableTransaction
{
    public static async Task<TResult> ExecuteAsync<TDbContext, TResult>(
        IDbContextFactory<TDbContext> factory,
        Func<TDbContext, Task<TResult>> action,
        CancellationToken cancellationToken = default,
        int maxAttempts = 4)
        where TDbContext : DbContext
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var result = await action(db);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception ex)
    {
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
        {
            if (cause is DbUpdateConcurrencyException or SqlException { Number: 1205 })
                return true;
        }
        return false;
    }
}
