using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Configuration;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Persists logs emitted inside an active workflow activity into the embedded
/// OTLP collector so logs and spans share the same trace id in SQLite.
/// </summary>
public sealed class CollectorLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly TelemetryIngestQueue _queue;
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public CollectorLoggerProvider(
        TelemetryIngestQueue queue,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings)
    {
        _queue = queue;
        _openTelemetrySettings = openTelemetrySettings;
    }

    public ILogger CreateLogger(string categoryName)
        => new CollectorLogger(categoryName, _queue, _openTelemetrySettings, () => _scopeProvider);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CollectorLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly TelemetryIngestQueue _queue;
        private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
        private readonly Func<IExternalScopeProvider> _scopeProviderAccessor;

        public CollectorLogger(
            string categoryName,
            TelemetryIngestQueue queue,
            IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
            Func<IExternalScopeProvider> scopeProviderAccessor)
        {
            _categoryName = categoryName;
            _queue = queue;
            _openTelemetrySettings = openTelemetrySettings;
            _scopeProviderAccessor = scopeProviderAccessor;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => _scopeProviderAccessor().Push(state);

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None
               && EmbeddedCollectorLogCategoryFilter.ShouldCapture(_categoryName)
               && !_categoryName.StartsWith(typeof(CollectorLoggerProvider).Namespace ?? string.Empty, StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var settings = _openTelemetrySettings.CurrentValue;
            var activity = Activity.Current;
            if (activity is null && !settings.Enabled)
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
                return;

            Guid? tenantId = null;
            if (!string.IsNullOrWhiteSpace(settings.TenantId) && Guid.TryParse(settings.TenantId, out var parsedTenantId))
                tenantId = parsedTenantId;

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["log.category"] = _categoryName,
                ["log.event_id"] = eventId.Id,
                ["log.event_name"] = eventId.Name
            };

            if (activity is not null)
            {
                attributes["trace_id"] = activity.TraceId.ToHexString();
                attributes["span_id"] = activity.SpanId.ToHexString();
            }

            if (exception is not null)
            {
                attributes["exception.type"] = exception.GetType().FullName;
                attributes["exception.message"] = exception.Message;
                attributes["exception.stacktrace"] = exception.StackTrace;
            }

            _scopeProviderAccessor().ForEachScope((scope, attributesState) =>
            {
                switch (scope)
                {
                    case IEnumerable<KeyValuePair<string, object?>> pairs:
                        foreach (var pair in pairs)
                        {
                            if (!string.IsNullOrWhiteSpace(pair.Key))
                                attributesState[$"scope.{pair.Key}"] = pair.Value;
                        }
                        break;
                    default:
                        attributesState[$"scope.{attributesState.Count}"] = scope?.ToString();
                        break;
                }
            }, attributes);

            var resource = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["service.name"] = settings.ServiceName,
                ["telemetry.sdk.language"] = "dotnet"
            };

            var scopeJson = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = _categoryName
            };

            var body = message;
            if (exception is not null && string.IsNullOrWhiteSpace(message))
                body = exception.ToString();

            var row = new LogRow(
                TenantId: tenantId,
                ReceivedUtc: DateTimeOffset.UtcNow,
                TraceId: activity is null ? null : Convert.FromHexString(activity.TraceId.ToHexString()),
                SpanId: activity is null ? null : Convert.FromHexString(activity.SpanId.ToHexString()),
                SeverityNumber: MapSeverity(logLevel),
                SeverityText: logLevel.ToString(),
                Body: body,
                AttributesJson: SerializeObject(attributes),
                ResourceJson: SerializeObject(resource),
                ScopeJson: SerializeObject(scopeJson),
                ServiceName: settings.ServiceName);

            if (!_queue.Channel.Writer.TryWrite(row))
                _queue.EnqueueAsync(row, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        private static int MapSeverity(LogLevel logLevel)
            => logLevel switch
            {
                LogLevel.Trace => 1,
                LogLevel.Debug => 5,
                LogLevel.Information => 9,
                LogLevel.Warning => 13,
                LogLevel.Error => 17,
                LogLevel.Critical => 21,
                _ => 0
            };

        private static string SerializeObject(IEnumerable<KeyValuePair<string, object?>> values)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer);

            writer.WriteStartObject();
            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                WriteObjectProperty(writer, pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static void WriteObjectProperty(Utf8JsonWriter writer, string propertyName, object? value)
        {
            if (value is null)
                return;

            if (value is JsonElement jsonElement
                && (jsonElement.ValueKind == JsonValueKind.Null || jsonElement.ValueKind == JsonValueKind.Undefined))
                return;

            writer.WritePropertyName(propertyName);
            WriteValue(writer, value);
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Undefined)
                {
                    writer.WriteNullValue();
                    return;
                }

                jsonElement.WriteTo(writer);
                return;
            }

            if (value is JsonDocument jsonDocument)
            {
                jsonDocument.RootElement.WriteTo(writer);
                return;
            }

            if (value is JsonNode jsonNode)
            {
                jsonNode.WriteTo(writer);
                return;
            }

            if (value is IEnumerable<KeyValuePair<string, object?>> objectPairs)
            {
                writer.WriteStartObject();
                foreach (var pair in objectPairs)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    WriteObjectProperty(writer, pair.Key, pair.Value);
                }

                writer.WriteEndObject();
                return;
            }

            if (value is IEnumerable<KeyValuePair<string, string?>> stringPairs)
            {
                writer.WriteStartObject();
                foreach (var pair in stringPairs)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        continue;

                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();
                return;
            }

            if (value is System.Collections.IDictionary dictionary)
            {
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key?.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    WriteObjectProperty(writer, key, entry.Value);
                }

                writer.WriteEndObject();
                return;
            }

            if (value is byte[] bytes)
            {
                writer.WriteBase64StringValue(bytes);
                return;
            }

            if (value is System.Collections.IEnumerable sequence && value is not string)
            {
                writer.WriteStartArray();
                foreach (var item in sequence)
                    WriteValue(writer, item);

                writer.WriteEndArray();
                return;
            }

            switch (value)
            {
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case char character:
                    writer.WriteStringValue(character.ToString());
                    break;
                case bool boolean:
                    writer.WriteBooleanValue(boolean);
                    break;
                case byte number:
                    writer.WriteNumberValue(number);
                    break;
                case sbyte number:
                    writer.WriteNumberValue(number);
                    break;
                case short number:
                    writer.WriteNumberValue(number);
                    break;
                case ushort number:
                    writer.WriteNumberValue(number);
                    break;
                case int number:
                    writer.WriteNumberValue(number);
                    break;
                case uint number:
                    writer.WriteNumberValue(number);
                    break;
                case long number:
                    writer.WriteNumberValue(number);
                    break;
                case ulong number:
                    writer.WriteNumberValue(number);
                    break;
                case float number:
                    writer.WriteNumberValue(number);
                    break;
                case double number:
                    writer.WriteNumberValue(number);
                    break;
                case decimal number:
                    writer.WriteNumberValue(number);
                    break;
                case Guid guid:
                    writer.WriteStringValue(guid);
                    break;
                case DateTime dateTime:
                    writer.WriteStringValue(dateTime);
                    break;
                case DateTimeOffset dateTimeOffset:
                    writer.WriteStringValue(dateTimeOffset);
                    break;
                case DateOnly dateOnly:
                    writer.WriteStringValue(dateOnly.ToString("O"));
                    break;
                case TimeOnly timeOnly:
                    writer.WriteStringValue(timeOnly.ToString("O"));
                    break;
                case Uri uri:
                    writer.WriteStringValue(uri.ToString());
                    break;
                case TimeSpan timeSpan:
                    writer.WriteStringValue(timeSpan.ToString());
                    break;
                case Enum enumValue:
                    writer.WriteStringValue(enumValue.ToString());
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }
    }
}


