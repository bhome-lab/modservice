using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModService.Host;

public sealed class SelfUpdateWorker(
    SelfUpdateService selfUpdateService,
    ILogger<SelfUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Running startup self-update check.");
            await selfUpdateService.CheckForUpdatesAsync("startup", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected self-update worker failure.");
        }
    }
}
