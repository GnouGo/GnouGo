namespace GnOuGo.Files.Server.Services;

public sealed class FilePurgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FilePurgeWorker> _logger;
    private readonly TimeSpan _interval;

    public FilePurgeWorker(
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Options.IOptions<Options.FilesServerOptions> options,
        ILogger<FilePurgeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.GetPurgeInterval();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeOnceAsync(stoppingToken);
        }
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<FileStorageService>();
            await storage.PurgeExpiredAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temporary file purge failed.");
        }
    }
}

