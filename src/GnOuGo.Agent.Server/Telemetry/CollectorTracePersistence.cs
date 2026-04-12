using System.Diagnostics;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Configuration;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Persists completed workflow activities into the embedded OTLP collector queue
/// so traces are stored in SQLite and exposed through the collector APIs.
/// </summary>
public sealed class CollectorTracePersistence
{
    private readonly TelemetryIngestQueue _queue;
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
    private readonly ILogger<CollectorTracePersistence> _logger;

    public CollectorTracePersistence(
        TelemetryIngestQueue queue,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
        ILogger<CollectorTracePersistence> logger)
    {
        _queue = queue;
        _openTelemetrySettings = openTelemetrySettings;
        _logger = logger;
    }

    public void Persist(Activity activity)
    {
        try
        {
            var settings = _openTelemetrySettings.CurrentValue;
            Guid? tenantId = null;
            if (!string.IsNullOrWhiteSpace(settings.TenantId) && Guid.TryParse(settings.TenantId, out var parsedTenantId))
                tenantId = parsedTenantId;

            var row = ActivityTelemetryMapper.ToSpanRow(activity, tenantId, settings.ServiceName);
            if (!_queue.Channel.Writer.TryWrite(row))
                _queue.EnqueueAsync(row, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist workflow activity '{ActivityName}' to the embedded OTLP collector.", activity.DisplayName);
        }
    }
}

