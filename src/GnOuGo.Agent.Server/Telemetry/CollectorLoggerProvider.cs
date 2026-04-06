using System.Diagnostics;
using System.Text.Json;
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
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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
               && !_categoryName.StartsWith("OtlpTenantCollector", StringComparison.Ordinal)
               && !_categoryName.StartsWith(typeof(CollectorLoggerProvider).Namespace ?? string.Empty, StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var activity = Activity.Current;
            if (activity is null)
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
                return;

            var settings = _openTelemetrySettings.CurrentValue;
            Guid? tenantId = null;
            if (!string.IsNullOrWhiteSpace(settings.TenantId) && Guid.TryParse(settings.TenantId, out var parsedTenantId))
                tenantId = parsedTenantId;

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["log.category"] = _categoryName,
                ["log.event_id"] = eventId.Id,
                ["log.event_name"] = eventId.Name,
                ["trace_id"] = activity.TraceId.ToHexString(),
                ["span_id"] = activity.SpanId.ToHexString()
            };

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
                TraceId: Convert.FromHexString(activity.TraceId.ToHexString()),
                SpanId: Convert.FromHexString(activity.SpanId.ToHexString()),
                SeverityNumber: MapSeverity(logLevel),
                SeverityText: logLevel.ToString(),
                Body: body,
                AttributesJson: JsonSerializer.Serialize(attributes, Json),
                ResourceJson: JsonSerializer.Serialize(resource, Json),
                ScopeJson: JsonSerializer.Serialize(scopeJson, Json),
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
    }
}


