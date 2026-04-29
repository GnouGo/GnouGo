using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Services.Routing;

public sealed class OptionsTelemetryRouter : ITelemetryRouter
{
    private readonly IOptionsMonitor<TelemetryRoutingOptions> _options;
    private readonly TelemetryRouteClassifier _classifier;
    private readonly OtlpHttpTelemetryForwarder _forwarder;
    private readonly ILogger<OptionsTelemetryRouter> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingTrace> _pendingTraces = new(StringComparer.OrdinalIgnoreCase);

    public OptionsTelemetryRouter(
        IOptionsMonitor<TelemetryRoutingOptions> options,
        TelemetryRouteClassifier classifier,
        OtlpHttpTelemetryForwarder forwarder,
        ILogger<OptionsTelemetryRouter> logger)
    {
        _options = options;
        _classifier = classifier;
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task RouteAsync(IReadOnlyList<SpanRow> spans, IReadOnlyList<LogRow> logs, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return;

        var immediate = new List<TelemetryBatch>();
        var now = DateTimeOffset.UtcNow;
        var traceBuffer = TimeSpan.FromSeconds(Math.Max(0, options.TraceBufferSeconds));

        lock (_sync)
        {
            foreach (var group in spans.GroupBy(span => ToTraceKey(span.TraceId)))
            {
                var pending = GetOrCreatePendingTrace(group.Key, now);
                pending.Spans.AddRange(group);
                pending.LastSeenUtc = now;
            }

            foreach (var log in logs)
            {
                var traceKey = log.TraceId is { Length: > 0 } ? ToTraceKey(log.TraceId) : null;
                if (traceKey is null)
                {
                    immediate.Add(new TelemetryBatch([], [log]));
                    continue;
                }

                var pending = GetOrCreatePendingTrace(traceKey, now);
                pending.Logs.Add(log);
                pending.LastSeenUtc = now;
            }

            immediate.AddRange(TakeExpiredLocked(now, traceBuffer));
        }

        await ForwardBatchesAsync(immediate, options, ct);
    }

    public async Task FlushExpiredAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return;

        var now = DateTimeOffset.UtcNow;
        var traceBuffer = TimeSpan.FromSeconds(Math.Max(0, options.TraceBufferSeconds));
        List<TelemetryBatch> batches;

        lock (_sync)
            batches = TakeExpiredLocked(now, traceBuffer);

        await ForwardBatchesAsync(batches, options, ct);
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return;

        List<TelemetryBatch> batches;
        lock (_sync)
        {
            batches = _pendingTraces.Values
                .Select(pending => new TelemetryBatch(pending.Spans.ToArray(), pending.Logs.ToArray()))
                .ToList();
            _pendingTraces.Clear();
        }

        await ForwardBatchesAsync(batches, options, ct);
    }

    private async Task ForwardBatchesAsync(List<TelemetryBatch> batches, TelemetryRoutingOptions options, CancellationToken ct)
    {
        foreach (var batch in batches)
        {
            if (batch.Spans.Count == 0 && batch.Logs.Count == 0)
                continue;

            var collectorName = _classifier.SelectCollector(batch.Spans, batch.Logs, options, _logger);
            if (string.IsNullOrWhiteSpace(collectorName))
                continue;

            if (!options.Collectors.TryGetValue(collectorName, out var collector))
            {
                _logger.LogWarning("Telemetry route selected unknown collector {Collector}", collectorName);
                continue;
            }

            try
            {
                await _forwarder.ForwardAsync(collectorName, collector, batch.Spans, batch.Logs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to forward telemetry batch to collector {Collector} (spans={Spans}, logs={Logs})",
                    collectorName,
                    batch.Spans.Count,
                    batch.Logs.Count);
            }
        }
    }

    private PendingTrace GetOrCreatePendingTrace(string traceKey, DateTimeOffset now)
    {
        if (_pendingTraces.TryGetValue(traceKey, out var pending))
            return pending;

        pending = new PendingTrace(now);
        _pendingTraces[traceKey] = pending;
        return pending;
    }

    private List<TelemetryBatch> TakeExpiredLocked(DateTimeOffset now, TimeSpan traceBuffer)
    {
        var expiredKeys = _pendingTraces
            .Where(pair => traceBuffer == TimeSpan.Zero || now - pair.Value.LastSeenUtc >= traceBuffer)
            .Select(pair => pair.Key)
            .ToArray();

        var batches = new List<TelemetryBatch>(expiredKeys.Length);
        foreach (var key in expiredKeys)
        {
            var pending = _pendingTraces[key];
            _pendingTraces.Remove(key);
            batches.Add(new TelemetryBatch(pending.Spans.ToArray(), pending.Logs.ToArray()));
        }

        return batches;
    }

    private static string ToTraceKey(byte[] traceId)
        => Convert.ToHexString(traceId);

    private sealed class PendingTrace
    {
        public PendingTrace(DateTimeOffset now)
        {
            LastSeenUtc = now;
        }

        public DateTimeOffset LastSeenUtc { get; set; }
        public List<SpanRow> Spans { get; } = [];
        public List<LogRow> Logs { get; } = [];
    }

    private sealed record TelemetryBatch(IReadOnlyList<SpanRow> Spans, IReadOnlyList<LogRow> Logs);
}

