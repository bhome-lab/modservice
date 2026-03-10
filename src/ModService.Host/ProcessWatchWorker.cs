using System.Diagnostics;
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
    ILogger<ProcessWatchWorker> logger) : BackgroundService
{
    private readonly HashSet<string> _observedProcesses = new(StringComparer.OrdinalIgnoreCase);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        SeedObservedProcesses();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configurationStore.TryGetCurrent(out var configuration))
                {
                    ObserveProcesses(configuration);
                }
                else
                {
                    logger.LogWarning("Skipping process watch because no valid configuration is available yet.");
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
            return;
        }

        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Core.Execution.SourceAsset>? assets = null;
        string? executorPath = null;

        foreach (var process in Process.GetProcesses())
        {
            if (!TryCreateSnapshot(process, out var snapshot))
            {
                continue;
            }

            var key = CreateProcessKey(snapshot);
            currentKeys.Add(key);
            if (_observedProcesses.Contains(key))
            {
                continue;
            }

            assets ??= syncService.LoadCurrentAssets();
            var plan = resolver.Resolve(configuration, snapshot, assets);
            if (plan is null)
            {
                _observedProcesses.Add(key);
                continue;
            }

            if (plan.ModulePaths.Count == 0)
            {
                logger.LogWarning(
                    "Matched process {ProcessName} (pid={Pid}) with rule {RuleName}, but no module paths are available yet.",
                    snapshot.ProcessName,
                    snapshot.ProcessId,
                    plan.RuleName);
                _observedProcesses.Add(key);
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
                _observedProcesses.Add(key);
                continue;
            }

            TryExecute(snapshot, plan, executorPath);
            _observedProcesses.Add(key);
        }

        _observedProcesses.RemoveWhere(key => !currentKeys.Contains(key));
    }

    private void SeedObservedProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            if (TryCreateProcessKey(process, out var key))
            {
                _observedProcesses.Add(key);
            }
        }

        logger.LogInformation(
            "Process watcher seeded with {ProcessCount} running processes; existing processes will not be activated on startup.",
            _observedProcesses.Count);
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

    private static bool TryCreateProcessKey(Process process, out string key)
    {
        key = string.Empty;

        try
        {
            key = $"{process.Id}|{(ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateSnapshot(Process process, out ProcessSnapshot snapshot)
    {
        snapshot = null!;

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
        catch
        {
            return false;
        }
    }
}
