using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Flow.Persistence;

/// <summary>Hosts supply their own configured tenant, stores and lease directories.</summary>
public sealed class OwnerRetentionWorker(Func<CancellationToken, Task> sweep, ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await sweep(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Owner collection failed ({ErrorType}).", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
