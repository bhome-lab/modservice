using Microsoft.Extensions.Options;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Host;

public sealed class ModServiceWorker(
    IOptionsMonitor<ModServiceConfiguration> optionsMonitor,
    SourceSyncService syncService,
    ILogger<ModServiceWorker> logger) : BackgroundService
{
    private IDisposable? _subscription;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LogConfiguration("startup", optionsMonitor.CurrentValue);
        _subscription = optionsMonitor.OnChange((configuration, _) => LogConfiguration("reload", configuration));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = optionsMonitor.CurrentValue;
            try
            {
                await SyncOnceAsync(configuration, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic sync failed.");
            }

            var nextDelay = PollingDelayCalculator.ComputeNextDelay(configuration.Polling);
            logger.LogInformation(
                "Next sync scheduled in {DelaySeconds} seconds (interval={IntervalSeconds}, jitter={JitterSeconds}).",
                (int)nextDelay.TotalSeconds,
                configuration.Polling.IntervalSeconds,
                configuration.Polling.JitterSeconds);
            await Task.Delay(nextDelay, stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return base.StopAsync(cancellationToken);
    }

    private void LogConfiguration(string reason, ModServiceConfiguration configuration)
    {
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Configuration {Reason} with {ErrorCount} validation errors: {Errors}",
                reason,
                errors.Count,
                string.Join(" | ", errors));
            return;
        }

        logger.LogInformation(
            "Configuration {Reason}: executor {ExecutorSource}/{ExecutorAsset}, {SourceCount} sources, {RuleCount} rules, polling {IntervalSeconds}s+{JitterSeconds}s jitter.",
            reason,
            configuration.Executor.Source,
            configuration.Executor.Asset,
            configuration.Sources.Count,
            configuration.Rules.Count,
            configuration.Polling.IntervalSeconds,
            configuration.Polling.JitterSeconds);
    }

    private async Task SyncOnceAsync(ModServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            logger.LogWarning("Skipping sync because configuration is invalid.");
            return;
        }

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
