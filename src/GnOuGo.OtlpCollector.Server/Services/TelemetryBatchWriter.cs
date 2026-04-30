using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Routing;

namespace OtlpTenantCollector.Services;

public sealed class TelemetryBatchWriter : BackgroundService
{
    private readonly AppOptions _opt;
    private readonly TelemetryIngestQueue _queue;
    private readonly TelemetryEventBus _eventBus;
    private readonly ITelemetryRouter _router;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryBatchWriter> _logger;

    public TelemetryBatchWriter(
        AppOptions opt, 
        TelemetryIngestQueue queue,
        TelemetryEventBus eventBus,
        ITelemetryRouter router,
        IServiceScopeFactory scopeFactory, 
        ILogger<TelemetryBatchWriter> logger)
    {
        _opt = opt;
        _queue = queue;
        _eventBus = eventBus;
        _router = router;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var spans = new List<SpanRow>(_opt.BatchSize);
        var logs  = new List<LogRow>(_opt.BatchSize);
        var reader = _queue.Channel.Reader;
        var flushInterval = TimeSpan.FromSeconds(_opt.FlushSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Utiliser un timeout au lieu de PeriodicTimer
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(flushInterval);

                    try
                    {
                        // Attendre qu'il y ait des données disponibles avec timeout
                        var hasData = await reader.WaitToReadAsync(timeoutCts.Token);
                        
                        if (!hasData)
                        {
                            // Channel fermé
                            await FlushAsync(spans, logs, stoppingToken);
                            return;
                        }

                        // Lire toutes les données disponibles
                        while (reader.TryRead(out var row))
                        {
                            if (row is SpanRow s) 
                            {
                                spans.Add(s);
                                _logger.LogDebug("Added span to batch: {SpanName}, TenantId={TenantId}", s.Name, s.TenantId);
                            }
                            else if (row is LogRow l) 
                            {
                                logs.Add(l);
                            }

                            if (spans.Count + logs.Count >= _opt.BatchSize)
                            {
                                await FlushAsync(spans, logs, stoppingToken);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        // Timeout atteint, flush les données
                        await FlushAsync(spans, logs, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Arrêt normal demandé
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TelemetryBatchWriter loop error - restarting");
                
                // Flush toute donnée en attente avant de redémarrer
                try
                {
                    await FlushAsync(spans, logs, stoppingToken);
                }
                catch (Exception flushEx)
                {
                    _logger.LogError(flushEx, "Failed to flush data after error");
                }
                
                await Task.Delay(1000, stoppingToken);
            }
        }

        await FlushAsync(spans, logs, CancellationToken.None);
        await _router.FlushAllAsync(CancellationToken.None);
    }

    private async Task FlushAsync(List<SpanRow> spans, List<LogRow> logs, CancellationToken ct)
    {
        if (spans.Count == 0 && logs.Count == 0)
        {
            await _router.FlushExpiredAsync(ct);
            return;
        }

        _logger.LogDebug("Starting flush: {SpanCount} spans, {LogCount} logs", spans.Count, logs.Count);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();

            // Convertir SpanRow -> SpanRecordEntity
            if (spans.Count > 0)
            {
                var spanEntities = spans.Select(TelemetryMapper.ToEntity).ToList();
                await store.AddSpansAsync(spanEntities);
            }

            // Convertir LogRow -> LogRecordEntity
            if (logs.Count > 0)
            {
                var logEntities = logs.Select(TelemetryMapper.ToEntity).ToList();
                await store.AddLogsAsync(logEntities);
            }

            _logger.LogDebug("Flushed {SpanCount} spans, {LogCount} logs", spans.Count, logs.Count);

            // Notify SSE subscribers that new data is available
            _eventBus.NotifyFlushed(spans.Count, logs.Count);

            await _router.RouteAsync(spans, logs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert telemetry batch (spans={Spans}, logs={Logs})", spans.Count, logs.Count);
        }
        finally
        {
            spans.Clear();
            logs.Clear();
        }
    }
}
