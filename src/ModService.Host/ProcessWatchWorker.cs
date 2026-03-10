using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Processes;
using ModService.Core.Updates;

namespace ModService.Host;

public sealed class ProcessWatchWorker(
    IOptionsMonitor<ModServiceConfiguration> optionsMonitor,
    SourceSyncService syncService,
    RuleResolver resolver,
    ILogger<ProcessWatchWorker> logger) : BackgroundService
{
    private readonly HashSet<string> _knownProcesses = new(StringComparer.OrdinalIgnoreCase);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        SeedKnownProcesses();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ObserveProcesses(optionsMonitor.CurrentValue);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Process watch scan failed.");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, optionsMonitor.CurrentValue.ProcessMonitoring.ScanIntervalSeconds));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private void SeedKnownProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            if (TryCreateSnapshot(process, out var snapshot))
            {
                _knownProcesses.Add(CreateProcessKey(snapshot));
            }
        }

        logger.LogInformation("Process watcher seeded with {ProcessCount} running processes.", _knownProcesses.Count);
    }

    private void ObserveProcesses(ModServiceConfiguration configuration)
    {
        if (!configuration.ProcessMonitoring.Enabled)
        {
            return;
        }

        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
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
            if (_knownProcesses.Contains(key))
            {
                continue;
            }

            assets ??= syncService.LoadCurrentAssets();
            executorPath ??= TryResolveExecutorPath(configuration);
            var plan = resolver.Resolve(configuration, snapshot, assets);
            if (plan is not null)
            {
                logger.LogInformation(
                    "Observed matching process {ProcessName} (pid={Pid}) with rule {RuleName}; executor={ExecutorPath}; modules={ModuleCount}. Observe-only mode does not activate external processes.",
                    snapshot.ProcessName,
                    snapshot.ProcessId,
                    plan.RuleName,
                    executorPath ?? "unavailable",
                    plan.ModulePaths.Count);
            }
        }

        _knownProcesses.Clear();
        foreach (var key in currentKeys)
        {
            _knownProcesses.Add(key);
        }
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

    private static string CreateProcessKey(ProcessSnapshot snapshot)
        => $"{snapshot.ProcessId}|{snapshot.ProcessCreateTimeUtc100ns}";

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
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
