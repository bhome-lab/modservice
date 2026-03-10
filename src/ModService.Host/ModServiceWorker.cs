using ModService.Core.Updates;

namespace ModService.Host;

public sealed class ModServiceWorker(
    EffectiveConfigurationStore configurationStore,
    SourceSyncService syncService,
    ILogger<ModServiceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configurationStore.TryGetCurrent(out var configuration))
                {
                    await SyncOnceAsync(configuration, stoppingToken);
                }
                else
                {
                    logger.LogWarning("Skipping sync because no valid configuration is available yet.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic sync failed.");
            }

            var nextDelay = configurationStore.GetSyncDelay();
            logger.LogInformation(
                "Next sync scheduled in {DelaySeconds} seconds.",
                (int)nextDelay.TotalSeconds);
            await Task.Delay(nextDelay, stoppingToken);
        }
    }

    private async Task SyncOnceAsync(
        ModService.Core.Configuration.ModServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var results = await syncService.SyncAsync(configuration, cancellationToken);
        foreach (var result in results)
        {
            logger.LogInformation(
                "Synced source {SourceId}: {AssetCount} assets, downloaded [{DownloadedAssets}]",
                result.Manifest.SourceId,
                result.Manifest.CurrentAssets.Count,
                string.Join(", ", result.DownloadedAssets));
        }

        var executorPath = syncService.ResolveExecutorPath(configuration);
        var cleanupResult = syncService.CleanupStaleFiles();
        logger.LogInformation(
            "Current executor path: {ExecutorPath}. Cleanup stale files: {StaleCount}, deleted={Deleted}, locked={LockedCount}.",
            executorPath,
            cleanupResult.StaleFileCount,
            cleanupResult.Deleted,
            cleanupResult.LockedFiles.Count);
    }
}
