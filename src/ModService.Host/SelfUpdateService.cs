using System.Threading;
using Microsoft.Extensions.Logging;
using ModService.Core.Configuration;
using ModService.GitHub.Auth;
using Velopack;
using Velopack.Sources;

namespace ModService.Host;

public sealed class SelfUpdateService
{
    private readonly EffectiveConfigurationStore _configurationStore;
    private readonly GitHubTokenStore _tokenStore;
    private readonly NotificationRequestQueue _notificationQueue;
    private readonly RuntimeStateStore _runtimeState;
    private readonly ILogger<SelfUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VelopackAsset? _pendingAsset;
    private int _restartDelaySeconds = 5;

    public SelfUpdateService(
        EffectiveConfigurationStore configurationStore,
        GitHubTokenStore tokenStore,
        NotificationRequestQueue notificationQueue,
        RuntimeStateStore runtimeState,
        ILogger<SelfUpdateService> logger)
    {
        _configurationStore = configurationStore;
        _tokenStore = tokenStore;
        _notificationQueue = notificationQueue;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    public int RestartDelaySeconds => Volatile.Read(ref _restartDelaySeconds);

    public async Task CheckForUpdatesAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_configurationStore.TryGetCurrent(out var configuration))
        {
            _pendingAsset = null;
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "disabled",
                Message = "Self-update is waiting for a valid configuration."
            });
            return;
        }

        if (!configuration.SelfUpdate.Enabled)
        {
            _pendingAsset = null;
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "disabled",
                Message = "Self-update is disabled."
            });
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(ref _restartDelaySeconds, configuration.SelfUpdate.RestartDelaySeconds);

            var sourceDescription = DescribeSource(configuration.SelfUpdate);
            var updateManager = CreateUpdateManager(configuration.SelfUpdate);
            var currentVersion = updateManager.CurrentVersion?.ToString();

            if (!updateManager.IsInstalled)
            {
                _pendingAsset = null;
                _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
                {
                    State = "unavailable",
                    Source = sourceDescription,
                    CurrentVersion = currentVersion,
                    Message = "Self-update is unavailable when ModService is running from an unpackaged build."
                });
                return;
            }

            if (updateManager.UpdatePendingRestart is { } pendingRestart)
            {
                _pendingAsset = pendingRestart;
                _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
                {
                    State = "ready_to_apply",
                    Source = sourceDescription,
                    CurrentVersion = currentVersion,
                    PreparedVersion = pendingRestart.Version.ToString(),
                    LastCheckedAtUtc = DateTimeOffset.UtcNow,
                    Message = $"Update {pendingRestart.Version} is downloaded and waiting to restart."
                });
                return;
            }

            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "checking",
                Source = sourceDescription,
                CurrentVersion = currentVersion,
                Message = reason.Equals("manual", StringComparison.OrdinalIgnoreCase)
                    ? "Checking for updates..."
                    : $"Checking for updates ({reason})..."
            });

            var updates = await updateManager.CheckForUpdatesAsync();
            var checkedAt = DateTimeOffset.UtcNow;

            if (updates is null)
            {
                _pendingAsset = null;
                _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
                {
                    State = "up_to_date",
                    Source = sourceDescription,
                    CurrentVersion = currentVersion,
                    LastCheckedAtUtc = checkedAt,
                    Message = "ModService is up to date."
                });
                return;
            }

            var targetVersion = updates.TargetFullRelease.Version.ToString();
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "downloading",
                Source = sourceDescription,
                CurrentVersion = currentVersion,
                AvailableVersion = targetVersion,
                LastCheckedAtUtc = checkedAt,
                Message = $"Downloading update {targetVersion}..."
            }, $"Update {targetVersion} is available.");

            await updateManager.DownloadUpdatesAsync(
                updates,
                progress => _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
                {
                    State = "downloading",
                    Source = sourceDescription,
                    CurrentVersion = currentVersion,
                    AvailableVersion = targetVersion,
                    LastCheckedAtUtc = checkedAt,
                    DownloadProgressPercent = progress,
                    Message = $"Downloading update {targetVersion} ({progress}%)."
                }),
                cancellationToken);

            _pendingAsset = updates.TargetFullRelease;
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "ready_to_apply",
                Source = sourceDescription,
                CurrentVersion = currentVersion,
                AvailableVersion = targetVersion,
                PreparedVersion = targetVersion,
                LastCheckedAtUtc = checkedAt,
                Message = configuration.SelfUpdate.RestartDelaySeconds == 0
                    ? $"Update {targetVersion} is downloaded and restarting now."
                    : $"Update {targetVersion} is downloaded and restarting in {configuration.SelfUpdate.RestartDelaySeconds} seconds."
            }, $"Update {targetVersion} downloaded and ready to install.");

            var timeoutMs = Math.Max(5_000, (configuration.SelfUpdate.RestartDelaySeconds + 2) * 1_000);
            var notificationText = configuration.SelfUpdate.RestartDelaySeconds == 0
                ? $"Update {targetVersion} downloaded. Restarting now."
                : $"Update {targetVersion} downloaded. Restarting in {configuration.SelfUpdate.RestartDelaySeconds} seconds.";
            _notificationQueue.Enqueue("ModService update ready", notificationText, timeoutMs: timeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Self-update check failed.");
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "error",
                Message = exception.Message
            }, $"Self-update failed: {exception.Message}");
            _notificationQueue.Enqueue("ModService update error", exception.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ApplyPreparedUpdateAndRestartAsync(CancellationToken cancellationToken)
    {
        if (!_configurationStore.TryGetCurrent(out var configuration) || !configuration.SelfUpdate.Enabled)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var updateManager = CreateUpdateManager(configuration.SelfUpdate);
            if (!updateManager.IsInstalled)
            {
                return false;
            }

            var pending = _pendingAsset ?? updateManager.UpdatePendingRestart;
            if (pending is null)
            {
                return false;
            }

            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "applying",
                Source = DescribeSource(configuration.SelfUpdate),
                CurrentVersion = updateManager.CurrentVersion?.ToString(),
                PreparedVersion = pending.Version.ToString(),
                LastCheckedAtUtc = DateTimeOffset.UtcNow,
                Message = $"Applying update {pending.Version} and restarting..."
            }, $"Applying update {pending.Version}.");

            await updateManager.WaitExitThenApplyUpdatesAsync(pending, silent: true, restart: true, []);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to apply a prepared update.");
            _runtimeState.SetSelfUpdateStatus(new SelfUpdateStatusSnapshot
            {
                State = "error",
                Message = exception.Message
            }, $"Applying the downloaded update failed: {exception.Message}");
            _notificationQueue.Enqueue("ModService update error", exception.Message, ToolTipIcon.Warning);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private UpdateManager CreateUpdateManager(SelfUpdateConfiguration configuration)
    {
        var options = new UpdateOptions();
        if (!string.IsNullOrWhiteSpace(configuration.Channel))
        {
            options.ExplicitChannel = configuration.Channel;
        }

        if (!string.IsNullOrWhiteSpace(configuration.FeedPath))
        {
            return new UpdateManager(configuration.FeedPath, options);
        }

        var accessToken = _tokenStore.TryLoadToken() ?? string.Empty;
        var source = new GithubSource(configuration.RepoUrl, accessToken, configuration.Prerelease);
        return new UpdateManager(source, options);
    }

    private static string DescribeSource(SelfUpdateConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.FeedPath))
        {
            return configuration.FeedPath;
        }

        return configuration.RepoUrl;
    }
}
