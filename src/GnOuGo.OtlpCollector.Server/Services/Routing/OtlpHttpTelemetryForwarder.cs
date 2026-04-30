using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Services.Routing;

public sealed class OtlpHttpTelemetryForwarder
{
    private static readonly DateTimeOffset UnixEpoch = DateTimeOffset.FromUnixTimeSeconds(0);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OtlpHttpTelemetryForwarder> _logger;

    public OtlpHttpTelemetryForwarder(IHttpClientFactory httpClientFactory, ILogger<OtlpHttpTelemetryForwarder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ForwardAsync(
        string collectorName,
        TelemetryCollectorOptions collector,
        IReadOnlyList<SpanRow> spans,
        IReadOnlyList<LogRow> logs,
        CancellationToken ct)
    {
        if (!collector.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(collector.Endpoint))
        {
            _logger.LogWarning("Telemetry collector {Collector} has no endpoint configured", collectorName);
            return;
        }

        foreach (var group in SplitByTenant(spans, logs))
        {
            if (group.Spans.Count > 0)
                await PostAsync(collectorName, collector, "v1/traces", BuildTraceRequest(group.Spans).ToByteArray(), group.TenantId, ct);

            if (group.Logs.Count > 0)
                await PostAsync(collectorName, collector, "v1/logs", BuildLogsRequest(group.Logs).ToByteArray(), group.TenantId, ct);
        }
    }

    private async Task PostAsync(
        string collectorName,
        TelemetryCollectorOptions collector,
        string relativePath,
        byte[] payload,
        Guid? tenantId,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(collector.Endpoint, relativePath));
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        foreach (var header in collector.Headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (collector.IncludeTenantHeader && tenantId is not null)
            request.Headers.TryAddWithoutValidation(OtlpTenantAuth.TenantIdHeader, tenantId.Value.ToString("D"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, collector.TimeoutSeconds)));

        var httpClient = _httpClientFactory.CreateClient(nameof(OtlpHttpTelemetryForwarder));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            _logger.LogWarning(
                "Collector {Collector} rejected {Path} with HTTP {StatusCode}. Body: {Body}",
                collectorName,
                relativePath,
                (int)response.StatusCode,
                body);
            return;
        }

        _logger.LogDebug(
            "Forwarded {Bytes} bytes to collector {Collector} at {Path}",
            payload.Length,
            collectorName,
            relativePath);
    }

    private static Uri BuildEndpoint(string endpoint, string relativePath)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase))
        {
            var baseEndpoint = trimmed[..trimmed.LastIndexOf("/v1/", StringComparison.OrdinalIgnoreCase)];
            return new Uri($"{baseEndpoint}/{relativePath}", UriKind.Absolute);
        }

        return new Uri($"{trimmed}/{relativePath}", UriKind.Absolute);
    }

    private static ExportTraceServiceRequest BuildTraceRequest(IReadOnlyList<SpanRow> spans)
    {
        var request = new ExportTraceServiceRequest();
        foreach (var span in spans)
        {
            var resourceSpans = new ResourceSpans
            {
                Resource = BuildResource(span.ResourceJson, span.ServiceName)
            };
            var scopeSpans = new ScopeSpans
            {
                Scope = BuildScope(span.ScopeJson)
            };

            var protoSpan = new Span
            {
                TraceId = ByteString.CopyFrom(span.TraceId),
                SpanId = ByteString.CopyFrom(span.SpanId),
                Name = span.Name,
                Kind = (Span.Types.SpanKind)span.Kind,
                StartTimeUnixNano = (ulong)Math.Max(0, span.StartUnixNs),
                EndTimeUnixNano = (ulong)Math.Max(0, span.EndUnixNs),
                Status = new Status
                {
                    Code = (Status.Types.StatusCode)span.StatusCode,
                    Message = span.StatusMessage ?? string.Empty
                }
            };

            if (span.ParentSpanId is { Length: > 0 })
                protoSpan.ParentSpanId = ByteString.CopyFrom(span.ParentSpanId);

            protoSpan.Attributes.AddRange(BuildKeyValues(span.AttributesJson));
            protoSpan.Events.AddRange(BuildEvents(span.EventsJson));
            scopeSpans.Spans.Add(protoSpan);
            resourceSpans.ScopeSpans.Add(scopeSpans);
            request.ResourceSpans.Add(resourceSpans);
        }

        return request;
    }

    private static ExportLogsServiceRequest BuildLogsRequest(IReadOnlyList<LogRow> logs)
    {
        var request = new ExportLogsServiceRequest();
        foreach (var log in logs)
        {
            var resourceLogs = new ResourceLogs
            {
                Resource = BuildResource(log.ResourceJson, log.ServiceName)
            };
            var scopeLogs = new ScopeLogs
            {
                Scope = BuildScope(log.ScopeJson)
            };

            var protoLog = new LogRecord
            {
                TimeUnixNano = ToUnixNanoseconds(log.ReceivedUtc),
                ObservedTimeUnixNano = ToUnixNanoseconds(log.ReceivedUtc),
                SeverityNumber = (SeverityNumber)log.SeverityNumber,
                SeverityText = log.SeverityText ?? string.Empty,
                Body = ToAnyValue(log.Body ?? string.Empty)
            };

            if (log.TraceId is { Length: > 0 })
                protoLog.TraceId = ByteString.CopyFrom(log.TraceId);
            if (log.SpanId is { Length: > 0 })
                protoLog.SpanId = ByteString.CopyFrom(log.SpanId);

            protoLog.Attributes.AddRange(BuildKeyValues(log.AttributesJson));
            scopeLogs.LogRecords.Add(protoLog);
            resourceLogs.ScopeLogs.Add(scopeLogs);
            request.ResourceLogs.Add(resourceLogs);
        }

        return request;
    }

    private static Resource BuildResource(string? resourceJson, string? serviceName)
    {
        var attributes = ReadObject(resourceJson);
        if (!string.IsNullOrWhiteSpace(serviceName) && !attributes.ContainsKey("service.name"))
            attributes["service.name"] = serviceName;

        var resource = new Resource();
        resource.Attributes.AddRange(BuildKeyValues(attributes));
        return resource;
    }

    private static InstrumentationScope BuildScope(string? scopeJson)
    {
        var attributes = ReadObject(scopeJson);
        var scope = new InstrumentationScope
        {
            Name = GetAndRemoveString(attributes, "name") ?? string.Empty,
            Version = GetAndRemoveString(attributes, "version") ?? string.Empty
        };
        scope.Attributes.AddRange(BuildKeyValues(attributes));
        return scope;
    }

    private static IEnumerable<Span.Types.Event> BuildEvents(string? eventsJson)
    {
        if (string.IsNullOrWhiteSpace(eventsJson))
            yield break;

        List<SpanEventDto> events;
        try
        {
            events = TelemetryJsonCodec.DeserializeSpanEvents(eventsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var spanEvent in events)
        {
            var protoEvent = new Span.Types.Event
            {
                Name = spanEvent.Name,
                TimeUnixNano = ToUnixNanoseconds(spanEvent.TimeUtc)
            };
            protoEvent.Attributes.AddRange(BuildKeyValues(spanEvent.Attributes));
            yield return protoEvent;
        }
    }

    private static IEnumerable<KeyValue> BuildKeyValues(string? json)
        => BuildKeyValues(ReadObject(json));

    private static IEnumerable<KeyValue> BuildKeyValues(Dictionary<string, object?> attributes)
    {
        foreach (var pair in attributes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                continue;

            yield return new KeyValue
            {
                Key = pair.Key,
                Value = ToAnyValue(pair.Value)
            };
        }
    }

    private static AnyValue ToAnyValue(object value)
    {
        if (value is JsonElement element)
            value = JsonElementToObject(element) ?? string.Empty;

        return value switch
        {
            string text => new AnyValue { StringValue = text },
            bool boolean => new AnyValue { BoolValue = boolean },
            byte number => new AnyValue { IntValue = number },
            sbyte number => new AnyValue { IntValue = number },
            short number => new AnyValue { IntValue = number },
            ushort number => new AnyValue { IntValue = number },
            int number => new AnyValue { IntValue = number },
            uint number => new AnyValue { IntValue = number },
            long number => new AnyValue { IntValue = number },
            ulong number when number <= long.MaxValue => new AnyValue { IntValue = (long)number },
            float number => new AnyValue { DoubleValue = number },
            double number => new AnyValue { DoubleValue = number },
            decimal number => new AnyValue { DoubleValue = (double)number },
            IEnumerable<KeyValuePair<string, object?>> pairs => new AnyValue { KvlistValue = ToKeyValueList(pairs) },
            System.Collections.IDictionary dictionary => new AnyValue { KvlistValue = ToKeyValueList(dictionary) },
            System.Collections.IEnumerable sequence when value is not string => new AnyValue { ArrayValue = ToArrayValue(sequence) },
            _ => new AnyValue { StringValue = value.ToString() ?? string.Empty }
        };
    }

    private static KeyValueList ToKeyValueList(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var list = new KeyValueList();
        list.Values.AddRange(BuildKeyValues(pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        return list;
    }

    private static KeyValueList ToKeyValueList(System.Collections.IDictionary dictionary)
    {
        var list = new KeyValueList();
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            var key = entry.Key.ToString();
            if (string.IsNullOrWhiteSpace(key) || entry.Value is null)
                continue;

            list.Values.Add(new KeyValue { Key = key, Value = ToAnyValue(entry.Value) });
        }

        return list;
    }

    private static ArrayValue ToArrayValue(System.Collections.IEnumerable sequence)
    {
        var array = new ArrayValue();
        foreach (var item in sequence)
        {
            if (item is not null)
                array.Values.Add(ToAnyValue(item));
        }

        return array;
    }

    private static Dictionary<string, object?> ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            return TelemetryJsonCodec.DeserializeObject(json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static object? JsonElementToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => JsonElementToObject(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    private static string? GetAndRemoveString(Dictionary<string, object?> values, string key)
    {
        if (!values.Remove(key, out var value) || value is null)
            return null;

        return value.ToString();
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset value)
    {
        var ticks = Math.Max(0, (value.ToUniversalTime() - UnixEpoch).Ticks);
        return (ulong)ticks * 100UL;
    }

    private static IEnumerable<TenantTelemetryBatch> SplitByTenant(IReadOnlyList<SpanRow> spans, IReadOnlyList<LogRow> logs)
    {
        var tenantIds = spans.Select(s => s.TenantId).Concat(logs.Select(l => l.TenantId)).Distinct().ToArray();
        foreach (var tenantId in tenantIds)
        {
            yield return new TenantTelemetryBatch(
                tenantId,
                spans.Where(s => s.TenantId == tenantId).ToArray(),
                logs.Where(l => l.TenantId == tenantId).ToArray());
        }
    }

    private sealed record TenantTelemetryBatch(Guid? TenantId, IReadOnlyList<SpanRow> Spans, IReadOnlyList<LogRow> Logs);
}



