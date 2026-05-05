using System.Diagnostics;
using System.Text.Json;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

public static class ActivityTelemetryMapper
{
    public static SpanRow ToSpanRow(Activity activity, Guid? tenantId, string serviceName)
    {
        var startUtc = activity.StartTimeUtc == default ? DateTime.UtcNow : activity.StartTimeUtc;
        var endUtc = activity.Duration > TimeSpan.Zero
            ? startUtc + activity.Duration
            : DateTime.UtcNow;

        if (endUtc < startUtc)
            endUtc = startUtc;

        var receivedUtc = DateTimeOffset.UtcNow;
        var startUnixNs = ToUnixNanoseconds(startUtc);
        var endUnixNs = ToUnixNanoseconds(endUtc);

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            if (!string.IsNullOrWhiteSpace(tag.Key))
                attributes[tag.Key] = NormalizeValue(tag.Value);
        }

        var events = new List<SpanEventDto>();
        foreach (var evt in activity.Events)
        {
            var eventAttrs = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in evt.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag.Key))
                    eventAttrs[tag.Key] = NormalizeValue(tag.Value);
            }

            events.Add(new SpanEventDto(evt.Name, evt.Timestamp.UtcDateTime, ToJsonElement(eventAttrs)));
        }

        var resource = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["service.name"] = serviceName,
            ["telemetry.sdk.language"] = "dotnet"
        };

        var scope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = activity.Source.Name,
            ["version"] = activity.Source.Version
        };

        return new SpanRow(
            TenantId: tenantId,
            ReceivedUtc: receivedUtc,
            TraceId: Convert.FromHexString(activity.TraceId.ToHexString()),
            SpanId: Convert.FromHexString(activity.SpanId.ToHexString()),
            ParentSpanId: activity.ParentSpanId != default ? Convert.FromHexString(activity.ParentSpanId.ToHexString()) : null,
            Name: string.IsNullOrWhiteSpace(activity.DisplayName) ? activity.OperationName : activity.DisplayName,
            Kind: MapKind(activity.Kind),
            StartUnixNs: startUnixNs,
            EndUnixNs: endUnixNs,
            StatusCode: MapStatus(activity.Status),
            StatusMessage: activity.StatusDescription,
            AttributesJson: TelemetryJsonCodec.SerializeObject(attributes),
            EventsJson: TelemetryJsonCodec.SerializeSpanEvents(events),
            ResourceJson: TelemetryJsonCodec.SerializeObject(resource),
            ScopeJson: TelemetryJsonCodec.SerializeObject(scope),
            ServiceName: serviceName);
    }

    private static long ToUnixNanoseconds(DateTime utc)
        => (utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L;

    private static object? NormalizeValue(object? value)
        => value switch
        {
            null => null,
            DateTimeOffset dto => dto,
            DateTime dt => dt,
            TimeSpan ts => ts.ToString(),
            ActivityTraceId traceId => traceId.ToHexString(),
            ActivitySpanId spanId => spanId.ToHexString(),
            _ => value
        };

    private static int MapStatus(ActivityStatusCode status)
        => status switch
        {
            ActivityStatusCode.Ok => 1,
            ActivityStatusCode.Error => 2,
            _ => 0
        };

    private static int MapKind(ActivityKind kind)
        => kind switch
        {
            ActivityKind.Internal => 1,
            ActivityKind.Server => 2,
            ActivityKind.Client => 3,
            ActivityKind.Producer => 4,
            ActivityKind.Consumer => 5,
            _ => 0
        };

    private static JsonElement ToJsonElement(Dictionary<string, object?> values)
    {
        var json = TelemetryJsonCodec.SerializeObject(values);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}


