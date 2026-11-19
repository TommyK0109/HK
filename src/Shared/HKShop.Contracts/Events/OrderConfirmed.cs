using System.Text.Json;

namespace HKShop.Contracts.Events;

public sealed record OrderConfirmed(string OrderNumber, string Recipient)
{
    public const string Topic = "hkshop.ordering.events.v1";
}

public sealed record EventEnvelope(Guid EventId, string Type, int Version, string AggregateType,
    string AggregateId, DateTimeOffset OccurredAt, string CorrelationId, string? CausationId, JsonElement Payload);
