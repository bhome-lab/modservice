using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModService.Core.Configuration;
using ModService.Core.Execution;
using ModService.Core.Matching;
using ModService.Core.Processes;
using ModService.Core.Updates;
using ModService.Interop.Native;

namespace ModService.Host;

public sealed class ProcessWatchWorker(
    EffectiveConfigurationStore configurationStore,
    SourceSyncService syncService,
    RuleResolver resolver,
    RuntimeStateStore runtimeState,
    TrayPreferencesStore preferencesStore,
    NotificationRequestQueue notificationQueue,
    ILogger<ProcessWatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _observedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private long _configurationVersion = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ResetStateIfConfigurationChanged();

                if (configurationStore.TryGetCurrent(out var configuration))
                {
                    ObserveProcesses(configuration);
                }
                else
                {
                    logger.LogWarning("Skipping process watch because no valid configuration is available yet.");
                    runtimeState.MarkProcessScan(
                        enabled: false,
                        accessibleProcessCount: 0,
                        inaccessibleProcessCount: 0,
                        inaccessibleSummary: "No valid configuration is available.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Process watch scan failed.");
            }

            var delay = configurationStore.GetProcessScanDelay();
            await Task.Delay(delay, stoppingToken);
        }
    }

    private void ObserveProcesses(ModServiceConfiguration configuration)
    {
        if (!configuration.ProcessMonitoring.Enabled)
        {
            runtimeState.MarkProcessScan(
                enabled: false,
                accessibleProcessCount: 0,
                inaccessibleProcessCount: 0,
                inaccessibleSummary: null);
            return;
        }

        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Core.Execution.SourceAsset>? assets = null;
        string? executorPath = null;
        var inaccessibleProcessCount = 0;
        string? inaccessibleSummary = null;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!TryCreateSnapshot(process, out var snapshot, out var failureReason))
                {
                    inaccessibleProcessCount++;
                    inaccessibleSummary ??= failureReason;
                    continue;
                }

                var key = CreateProcessKey(snapshot);
                currentKeys.Add(key);
                if (_observedProcesses.Contains(key) || IsRetryDeferred(key))
                {
                    continue;
                }

                assets ??= syncService.LoadCurrentAssets();
                var plan = resolver.Resolve(configuration, snapshot, assets);
                if (plan is null)
                {
                    _observedProcesses.Add(key);
                    _retryAfter.Remove(key);
                    continue;
                }

                if (plan.ModulePaths.Count == 0)
                {
                    logger.LogWarning(
                        "Matched process {ProcessName} (pid={Pid}) with rule {RuleName}, but no module paths are available yet.",
                        snapshot.ProcessName,
                        snapshot.ProcessId,
                        plan.RuleName);
                    ScheduleRetry(key);
                    continue;
                }

                executorPath ??= TryResolveExecutorPath(configuration);
                if (string.IsNullOrWhiteSpace(executorPath))
                {
                    logger.LogWarning(
                        "Matched process {ProcessName} (pid={Pid}) with rule {RuleName}, but the executor is not available yet.",
                        snapshot.ProcessName,
                        snapshot.ProcessId,
                        plan.RuleName);
                    ScheduleRetry(key);
                    continue;
                }

                if (TryExecute(snapshot, plan, executorPath))
                {
                    _observedProcesses.Add(key);
                    _retryAfter.Remove(key);

                    var summary = $"Processed {snapshot.ProcessName} (pid={snapshot.ProcessId}) with rule {plan.RuleName}.";
                    runtimeState.MarkActivation(summary);
                    if (preferencesStore.AreProcessNotificationsEnabled())
                    {
                        notificationQueue.Enqueue("ModService", summary);
                    }
                }
                else
                {
                    ScheduleRetry(key);
                    runtimeState.MarkActivation(
                        $"Failed to process {snapshot.ProcessName} (pid={snapshot.ProcessId}) with rule {plan.RuleName}; retry scheduled.");
                }
            }
        }

        _observedProcesses.RemoveWhere(key => !currentKeys.Contains(key));
        foreach (var key in _retryAfter.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _retryAfter.Remove(key);
        }

        runtimeState.MarkProcessScan(
            enabled: true,
            accessibleProcessCount: currentKeys.Count,
            inaccessibleProcessCount: inaccessibleProcessCount,
            inaccessibleSummary: inaccessibleSummary);
    }

    private string? TryResolveExecutorPath(ModServiceConfiguration configuration)
    {
        try
        {
            return syncService.ResolveExecutorPath(configuration);
        }
        catch
        {
            return null;
        }
    }

    private bool TryExecute(ProcessSnapshot snapshot, ResolvedExecutionPlan plan, string executorPath)
    {
        try
        {
            using var client = new NativeExecutorClient(executorPath);
            var result = client.Execute(new NativeExecuteRequest
            {
                ProcessId = (uint)snapshot.ProcessId,
                ProcessCreateTimeUtc100ns = snapshot.ProcessCreateTimeUtc100ns,
                ExecutablePath = snapshot.ExePath,
                ModulePaths = plan.ModulePaths,
                EnvironmentVariables = plan.EnvironmentVariables
                    .Select(item => new NativeEnvironmentVariable { Name = item.Name, Value = item.Value })
                    .ToArray(),
                ExecutorOptions = plan.ExecutorOptions
                    .Select(item => new NativeExecutorOption { Name = item.Name, Value = item.Value })
                    .ToArray(),
                TimeoutMs = 1_000
            });

            if (!result.IsSuccess)
            {
                logger.LogWarning(
                    "Executor failed for process {ProcessName} (pid={Pid}) with rule {RuleName}: status={Status}, error={Error}.",
                    snapshot.ProcessName,
                    snapshot.ProcessId,
                    plan.RuleName,
                    result.Status,
                    result.ErrorText ?? string.Empty);
                return false;
            }

            logger.LogInformation(
                "Activated process {ProcessName} (pid={Pid}) with rule {RuleName}; executor={ExecutorPath}; modules={ModuleCount}; env={EnvironmentCount}.",
                snapshot.ProcessName,
                snapshot.ProcessId,
                plan.RuleName,
                executorPath,
                plan.ModulePaths.Count,
                plan.EnvironmentVariables.Count);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Executor invocation failed for process {ProcessName} (pid={Pid}) with rule {RuleName}.",
                snapshot.ProcessName,
                snapshot.ProcessId,
                plan.RuleName);
            return false;
        }
    }

    private static string CreateProcessKey(ProcessSnapshot snapshot)
        => $"{snapshot.ProcessId}|{snapshot.ProcessCreateTimeUtc100ns}";

    private void ResetStateIfConfigurationChanged()
    {
        var status = configurationStore.GetStatus();
        if (status.Version == _configurationVersion)
        {
            return;
        }

        _observedProcesses.Clear();
        _retryAfter.Clear();
        _configurationVersion = status.Version;
        logger.LogInformation("Process watcher state reset for configuration version {Version}.", _configurationVersion);
    }

    private bool IsRetryDeferred(string key)
    {
        return _retryAfter.TryGetValue(key, out var retryAfter) &&
               retryAfter > DateTimeOffset.UtcNow;
    }

    private void ScheduleRetry(string key)
        => _retryAfter[key] = DateTimeOffset.UtcNow + RetryDelay;

    private static bool TryCreateSnapshot(Process process, out ProcessSnapshot snapshot, out string? failureReason)
    {
        snapshot = null!;
        failureReason = null;

        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            snapshot = new ProcessSnapshot
            {
                ProcessId = process.Id,
                ProcessName = Path.GetFileName(executablePath),
                ExePath = executablePath,
                ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                Environment = ProcessEnvironmentReader.Read(process)
            };
            return true;
        }
        catch (Exception exception)
        {
            failureReason = exception.Message;
            return false;
        }
    }
}
