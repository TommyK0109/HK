using System.Text.Json;

namespace HKShop.Domain.Orders
{
    public sealed class OutboxMessage
    {
        public long Id { get; set; }
        public string EventKey { get; set; } = "";
        public string Recipient { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTimeOffset NextAttemptAt { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? SentAt { get; set; }
    }

    public sealed class IntegrationOutboxEvent
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

    public sealed class ConsumedIntegrationEvent
    {
        public long Id { get; set; }
        public string ConsumerName { get; set; } = "";
        public string EventKey { get; set; } = "";
        public DateTimeOffset ConsumedAt { get; set; }
    }

    public sealed class AuditEntry
    {
        public long Id { get; set; }
        public long? SellerId { get; set; }
        public string? ActorId { get; set; }
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTimeOffset OccurredAt { get; set; }
    }
}

namespace HKShop.Application
{
    using HKShop.Domain.Orders;

    public static class ShopRecords
    {
        public static void Audit(this IShopSession db, string? actor, string action, string target, DateTimeOffset now,
            string reason = "", long? sellerId = null) =>
            db.Add(new AuditEntry { ActorId = actor, Action = action, Target = target, Reason = reason, OccurredAt = now, SellerId = sellerId });

        public static void Notify(this IShopSession db, Order order, string eventKey, string subject, DateTimeOffset now) =>
            db.Add(new OutboxMessage
            {
                EventKey = $"{order.Id:N}:{eventKey}", Recipient = order.CustomerEmail,
                Subject = subject, Body = $"{subject}\nOrder: {order.Number}", NextAttemptAt = now
            });

        public static void IntegrationEvent(this IShopSession db, string aggregateType, string aggregateId,
            string eventType, object payload, DateTimeOffset now, string idempotencyKey) =>
            db.Add(new IntegrationOutboxEvent
            {
                AggregateType = aggregateType,
                AggregateId = aggregateId,
                EventType = eventType,
                PayloadJson = JsonSerializer.Serialize(payload),
                OccurredAt = now,
                IdempotencyKey = idempotencyKey
            });
    }
}
