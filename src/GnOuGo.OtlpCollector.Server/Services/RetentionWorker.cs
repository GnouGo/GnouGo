namespace OtlpTenantCollector.Services;

public sealed class RetentionWorker : BackgroundService
{
    private readonly AppOptions _opt;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(AppOptions opt, IServiceScopeFactory scopeFactory, ILogger<RetentionWorker> logger)
    {
        _opt = opt;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opt.RetentionSweepSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();

                // Récupérer tous les tenants
                var tenants = await store.GetAllTenantsAsync();
                var totalDeleted = 0;

                foreach (var tenant in tenants)
                {
                    var cutoffTime = DateTimeOffset.UtcNow.AddMinutes(-tenant.RetentionMinutes);
                    
                    var deletedSpans = await store.DeleteOldSpansAsync(tenant.Id, cutoffTime);
                    var deletedLogs = await store.DeleteOldLogsAsync(tenant.Id, cutoffTime);
                    
                    totalDeleted += deletedSpans + deletedLogs;

                    if (deletedSpans + deletedLogs > 0)
                    {
                        _logger.LogInformation(
                            "Tenant {TenantId}: deleted {Spans} spans, {Logs} logs (retention={Minutes}min)",
                            tenant.Id, deletedSpans, deletedLogs, tenant.RetentionMinutes);
                    }
                }

                if (totalDeleted > 0)
                {
                    _logger.LogInformation("Retention sweep deleted {Deleted} rows total", totalDeleted);
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "Retention sweep was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed");
            }
        }
    }
}
