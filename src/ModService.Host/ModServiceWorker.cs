using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Host;

public sealed class ModServiceWorker(
    EffectiveConfigurationStore configurationStore,
    SourceSyncService syncService,
    RuntimeStateStore runtimeState,
    SelfUpdateService selfUpdateService,
    ILogger<ModServiceWorker> logger) : BackgroundService, IRefreshController
{
    private static readonly TimeSpan DefaultGitHubRateLimitBackoff = TimeSpan.FromMinutes(5);

    private readonly Channel<RefreshRequest> _refreshQueue = Channel.CreateUnbounded<RefreshRequest>();
    private int _queuedRefreshCount;
    private GitHubRateLimitStatusSnapshot? _gitHubRateLimitStatus;

    public void QueueRefresh(string reason)
    {
        var queuedRefreshCount = Interlocked.Increment(ref _queuedRefreshCount);
        runtimeState.MarkRefreshQueued(reason, queuedRefreshCount);
        _refreshQueue.Writer.TryWrite(new RefreshRequest(reason));
        _ = TriggerManualSelfUpdateCheckAsync(reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await _refreshQueue.Reader.ReadAsync(stoppingToken);
                var remaining = Math.Max(0, Interlocked.Decrement(ref _queuedRefreshCount));
                runtimeState.SetQueuedRefreshCount(remaining);
                await RunRefreshAsync(request.Reason, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunRefreshAsync(string reason, CancellationToken cancellationToken)
    {
        runtimeState.MarkRefreshStarted(reason, Math.Max(0, Volatile.Read(ref _queuedRefreshCount)));
        ModServiceConfiguration? configuration = null;

        try
        {
            if (!configurationStore.TryGetCurrent(out configuration))
            {
                var configurationStatus = configurationStore.GetStatus();
                var errorSummary = configurationStatus.ValidationErrors.Count == 0
                    ? "No valid configuration is available."
                    : string.Join(" | ", configurationStatus.ValidationErrors);

                logger.LogWarning("Skipping sync because no valid configuration is available yet.");
                runtimeState.MarkRefreshCompleted(
                    reason,
                    success: false,
                    "Refresh skipped because the configuration is invalid.",
                    errorSummary,
                    executorPath: null,
                    sources: [],
                    cleanup: new CleanupStatusSnapshot());
                return;
            }

            if (GetGitHubBackoffUntilUtc() is { } backoffUntilUtc && backoffUntilUtc > DateTimeOffset.UtcNow)
            {
                var retryAfterSeconds = GetRetryAfterSeconds(backoffUntilUtc);
                var backoffSummary = $"Refresh skipped because GitHub rate-limit backoff is active for another {retryAfterSeconds} second(s).";
                logger.LogWarning("{Summary}", backoffSummary);

                runtimeState.MarkRefreshCompleted(
                    reason,
                    success: true,
                    backoffSummary,
                    error: null,
                    TryResolveExecutorPath(configuration),
                    BuildSourceStatuses(configuration),
                    runtimeState.GetSnapshot().Cleanup);
                return;
            }

            var results = await syncService.SyncAsync(configuration, cancellationToken);
            _gitHubRateLimitStatus = null;
            runtimeState.SetGitHubReady();

            foreach (var result in results)
            {
                logger.LogInformation(
                    "Synced source {SourceId}: {AssetCount} assets, downloaded [{DownloadedAssets}]",
                    result.Manifest.SourceId,
                    result.Manifest.CurrentAssets.Count,
                    string.Join(", ", result.DownloadedAssets));
            }

            var executorPath = TryResolveExecutorPath(configuration);
            var cleanupResult = syncService.CleanupStaleFiles();
            logger.LogInformation(
                "Current executor path: {ExecutorPath}. Cleanup stale files: {StaleCount}, deleted={Deleted}, locked={LockedCount}.",
                executorPath ?? "Unavailable",
                cleanupResult.StaleFileCount,
                cleanupResult.Deleted,
                cleanupResult.LockedFiles.Count);

            var sourceStatuses = BuildSourceStatuses(configuration);
            var summary = BuildRefreshSummary(results, cleanupResult);
            runtimeState.MarkRefreshCompleted(
                reason,
                success: true,
                summary,
                error: null,
                executorPath,
                sourceStatuses,
                ToCleanupSnapshot(cleanupResult));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubRateLimitException exception)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var backoffUntilUtc = exception.GetBackoffUntilUtc(nowUtc, DefaultGitHubRateLimitBackoff);
            _gitHubRateLimitStatus = new GitHubRateLimitStatusSnapshot
            {
                Limit = exception.Limit,
                Remaining = exception.Remaining,
                ResetAtUtc = exception.ResetAtUtc,
                BackoffUntilUtc = backoffUntilUtc,
                Scope = exception.Scope,
                Message = exception.Message
            };

            runtimeState.SetGitHubRateLimited(
                exception.Limit,
                exception.Remaining,
                exception.ResetAtUtc,
                backoffUntilUtc,
                exception.Scope,
                exception.Message);

            var summary = $"Refresh paused because the GitHub API rate limit was exceeded. Backoff until {backoffUntilUtc.ToLocalTime():G}.";
            logger.LogWarning(exception, "{Summary}", summary);
            runtimeState.MarkRefreshCompleted(
                reason,
                success: true,
                summary,
                error: null,
                TryResolveExecutorPath(configuration!),
                BuildSourceStatuses(configuration!),
                cleanup: runtimeState.GetSnapshot().Cleanup);
        }
        catch (Exception exception)
        {
            runtimeState.SetGitHubError(exception.Message);
            logger.LogError(exception, "Refresh failed for reason {Reason}.", reason);
            runtimeState.MarkRefreshCompleted(
                reason,
                success: false,
                $"Refresh failed ({reason}).",
                exception.Message,
                executorPath: null,
                sources: [],
                cleanup: new CleanupStatusSnapshot());
        }
    }

    private DateTimeOffset? GetGitHubBackoffUntilUtc()
        => _gitHubRateLimitStatus?.BackoffUntilUtc;

    private static int GetRetryAfterSeconds(DateTimeOffset backoffUntilUtc)
        => Math.Max(0, (int)Math.Ceiling((backoffUntilUtc - DateTimeOffset.UtcNow).TotalSeconds));

    private IReadOnlyList<SourceStatusSnapshot> BuildSourceStatuses(ModServiceConfiguration configuration)
    {
        return configuration.Sources
            .OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .Select(source =>
            {
                var manifest = syncService.LoadManifest(source.Id);
                return manifest is null
                    ? new SourceStatusSnapshot
                    {
                        SourceId = source.Id,
                        Status = "No manifest available yet."
                    }
                    : new SourceStatusSnapshot
                    {
                        SourceId = source.Id,
                        SyncedAtUtc = manifest.SyncedAtUtc,
                        AssetCount = manifest.CurrentAssets.Count,
                        Status = "Ready"
                    };
            })
            .ToArray();
    }

    private static CleanupStatusSnapshot ToCleanupSnapshot(CleanupResult cleanupResult)
    {
        return new CleanupStatusSnapshot
        {
            StaleFileCount = cleanupResult.StaleFileCount,
            LockedFileCount = cleanupResult.LockedFiles.Count,
            Deleted = cleanupResult.Deleted
        };
    }

    private static string BuildRefreshSummary(IReadOnlyList<SourceSyncResult> results, CleanupResult cleanupResult)
    {
        var downloadedAssets = results.Sum(result => result.DownloadedAssets.Count);
        return $"Synced {results.Count} source(s), downloaded {downloadedAssets} asset(s), stale files {cleanupResult.StaleFileCount}, locked stale files {cleanupResult.LockedFiles.Count}.";
    }

    private string? TryResolveExecutorPath(ModServiceConfiguration configuration)
    {
        try
        {
            return syncService.ResolveExecutorPath(configuration);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Executor path is not available after refresh.");
            return null;
        }
    }

    private async Task TriggerManualSelfUpdateCheckAsync(string reason)
    {
        try
        {
            await selfUpdateService.CheckForUpdatesAsync("manual", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Manual self-update check failed for refresh reason {Reason}.", reason);
        }
    }

    private sealed record RefreshRequest(string Reason);
}
