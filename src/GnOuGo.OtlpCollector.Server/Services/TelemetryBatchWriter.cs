using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

public sealed class TelemetryBatchWriter : BackgroundService
{
    private readonly AppOptions _opt;
    private readonly TelemetryIngestQueue _queue;
    private readonly TelemetryEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryBatchWriter> _logger;

    public TelemetryBatchWriter(
        AppOptions opt, 
        TelemetryIngestQueue queue,
        TelemetryEventBus eventBus,
        IServiceScopeFactory scopeFactory, 
        ILogger<TelemetryBatchWriter> logger)
    {
        _opt = opt;
        _queue = queue;
        _eventBus = eventBus;
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
                            Flush(spans, logs);
                            return;
                        }

                        // Lire toutes les données disponibles
                        while (reader.TryRead(out var row))
                        {
                            if (row is SpanRow s) 
                            {
                                spans.Add(s);
                                _logger.LogInformation("Added span to batch: {SpanName}, TenantId={TenantId}", s.Name, s.TenantId);
                            }
                            else if (row is LogRow l) 
                            {
                                logs.Add(l);
                            }

                            if (spans.Count + logs.Count >= _opt.BatchSize)
                            {
                                Flush(spans, logs);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        // Timeout atteint, flush les données
                        Flush(spans, logs);
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
                    Flush(spans, logs);
                }
                catch (Exception flushEx)
                {
                    _logger.LogError(flushEx, "Failed to flush data after error");
                }
                
                await Task.Delay(1000, stoppingToken);
            }
        }

        Flush(spans, logs);
    }

    private void Flush(List<SpanRow> spans, List<LogRow> logs)
    {
        if (spans.Count == 0 && logs.Count == 0) return;

        _logger.LogInformation("Starting flush: {SpanCount} spans, {LogCount} logs", spans.Count, logs.Count);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();

            // Convertir SpanRow -> SpanRecordEntity
            if (spans.Count > 0)
            {
                var spanEntities = spans.Select(TelemetryMapper.ToEntity).ToList();
                _logger.LogInformation("Inserting {Count} span entities into database", spanEntities.Count);
                store.AddSpansAsync(spanEntities).GetAwaiter().GetResult();
                _logger.LogInformation("Successfully inserted {Count} spans", spanEntities.Count);
            }

            // Convertir LogRow -> LogRecordEntity
            if (logs.Count > 0)
            {
                var logEntities = logs.Select(TelemetryMapper.ToEntity).ToList();
                store.AddLogsAsync(logEntities).GetAwaiter().GetResult();
            }

            _logger.LogInformation("Flushed batch successfully: {SpanCount} spans, {LogCount} logs", spans.Count, logs.Count);

            // Notify SSE subscribers that new data is available
            _eventBus.NotifyFlushed(spans.Count, logs.Count);
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
