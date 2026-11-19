using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;

namespace HKShop.ServiceDefaults;

public static class HKShopTelemetry
{
    public const string ActivitySourceName = "HKShop";
    public const string MeterName = "HKShop";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static void ConfigureW3CPropagation()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        DistributedContextPropagator.Current = DistributedContextPropagator.CreateDefaultPropagator();
    }
}

public static class KafkaTelemetry
{
    public static Headers InjectTraceContext(Headers? headers = null, Activity? activity = null)
    {
        headers ??= new Headers();
        var current = activity ?? Activity.Current;
        if (current is null)
            return headers;

        DistributedContextPropagator.Current.Inject(current, headers, static (carrier, key, value) =>
        {
            if (carrier is not Headers target || string.IsNullOrEmpty(key) || value is null)
                return;
            target.Remove(key);
            target.Add(key, Encoding.UTF8.GetBytes(value));
        });

        return headers;
    }

    public static ActivityContext ExtractTraceContext(Headers? headers, out IEnumerable<KeyValuePair<string, string?>> baggage)
    {
        if (headers is null || headers.Count == 0)
        {
            baggage = [];
            return default;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(headers, ExtractHeader, out var traceParent, out var traceState);
        baggage = DistributedContextPropagator.Current.ExtractBaggage(headers, ExtractHeader) ?? [];

        return !string.IsNullOrWhiteSpace(traceParent) &&
               ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context)
            ? context
            : default;
    }

    public static Activity? StartConsumerActivity(Headers? headers, string operationName, string? topic = null, string? messageKey = null)
    {
        var parentContext = ExtractTraceContext(headers, out var baggage);
        var activity = parentContext != default
            ? HKShopTelemetry.ActivitySource.StartActivity(operationName, ActivityKind.Consumer, parentContext)
            : HKShopTelemetry.ActivitySource.StartActivity(operationName, ActivityKind.Consumer);

        if (activity is null)
            return null;

        foreach (var item in baggage)
            activity.SetBaggage(item.Key, item.Value);

        activity.SetTag("messaging.system", "kafka");
        if (!string.IsNullOrWhiteSpace(topic))
            activity.SetTag("messaging.destination.name", topic);
        if (!string.IsNullOrWhiteSpace(messageKey))
            activity.SetTag("messaging.kafka.message.key", messageKey);

        return activity;
    }

    public static Activity? StartProducerActivity(string operationName, Headers headers, string? topic = null, string? messageKey = null)
    {
        var activity = HKShopTelemetry.ActivitySource.StartActivity(operationName, ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", "kafka");
            if (!string.IsNullOrWhiteSpace(topic))
                activity.SetTag("messaging.destination.name", topic);
            if (!string.IsNullOrWhiteSpace(messageKey))
                activity.SetTag("messaging.kafka.message.key", messageKey);
        }

        InjectTraceContext(headers, activity ?? Activity.Current);
        return activity;
    }

    private static void ExtractHeader(object? carrier, string fieldName, out string? fieldValue, out IEnumerable<string>? fieldValues)
    {
        fieldValue = null;
        fieldValues = null;
        if (carrier is not Headers headers)
            return;

        List<string>? matches = null;
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (!string.Equals(header.Key, fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = header.GetValueBytes() is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
            fieldValue ??= value;
            matches ??= [];
            matches.Add(value);
        }

        fieldValues = matches;
    }
}
