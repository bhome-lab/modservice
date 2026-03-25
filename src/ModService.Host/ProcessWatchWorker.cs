using System.Diagnostics;
using System.Threading.Channels;
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
    IProcessEventSource processEventSource,
    ILogger<ProcessWatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan WarmupRetryDelay = TimeSpan.FromMilliseconds(350);
    private const int MaxWarmupAttempts = 2;

    private readonly ProcessObservationState _state = new();
    private readonly Channel<ProcessObservationRequest> _requests = Channel.CreateUnbounded<ProcessObservationRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var eventSubscription = TryStartProcessEventMonitoring();
        RefreshMonitoringStatus();

        try
        {
            while (await _requests.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_requests.Reader.TryRead(out var request))
                {
                    HandleObservationRequest(request, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Writer.TryComplete();
        }
    }

    private IDisposable? TryStartProcessEventMonitoring()
    {
        try
        {
            var subscription = processEventSource.Start(EnqueueProcessEvent);
            logger.LogInformation("System process event monitoring is active.");
            return subscription;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "System process event monitoring could not be started. Existing and new processes will not be captured until the host restarts successfully.");
            runtimeState.SetProcessMonitoringStatus(
                enabled: false,
                summary: "System process event monitoring could not be started.");
            return null;
        }
    }

    private void EnqueueProcessEvent(ProcessEventNotification notification)
    {
        var request = notification.Kind switch
        {
            ProcessEventKind.Started => ProcessObservationRequest.ProcessStarted(
                notification.ProcessId,
                notification.ProcessName),
            ProcessEventKind.Exited => ProcessObservationRequest.ProcessExited(notification.ProcessId),
            _ => default
        };

        if (request.Kind != ProcessObservationKind.None)
        {
            _requests.Writer.TryWrite(request);
        }
    }

    private void HandleObservationRequest(ProcessObservationRequest request, CancellationToken stoppingToken)
    {
        var configurationStatus = configurationStore.GetStatus();
        if (_state.ResetForConfiguration(configurationStatus.Version))
        {
            logger.LogInformation("Process watcher state reset for configuration version {Version}.", configurationStatus.Version);
            RefreshMonitoringStatus();
        }

        if (request.Kind == ProcessObservationKind.ProcessExited)
        {
            _state.RemoveProcess(request.ProcessId);
            return;
        }

        if (!configurationStore.TryGetCurrent(out var configuration))
        {
            return;
        }

        if (!configuration.ProcessMonitoring.Enabled)
        {
            return;
        }

        if (request.ConfigurationVersion is { } requestConfigurationVersion &&
            requestConfigurationVersion != configurationStatus.Version)
        {
            logger.LogDebug(
                "Ignoring stale process retry for pid={Pid} because configuration version {RequestVersion} is no longer active.",
                request.ProcessId,
                requestConfigurationVersion);
            return;
        }

        ObserveProcess(configuration, configurationStatus.Version, request, stoppingToken);
    }

    private void ObserveProcess(
        ModServiceConfiguration configuration,
        long configurationVersion,
        ProcessObservationRequest request,
        CancellationToken stoppingToken)
    {
        try
        {
            using var process = Process.GetProcessById(request.ProcessId);
            if (!TryCreateSnapshot(process, out var snapshot, out var failureReason))
            {
                ScheduleWarmupRetry(request, configurationVersion, failureReason, stoppingToken);
                return;
            }

            if (!MatchesObservedProcess(request, snapshot))
            {
                logger.LogDebug(
                    "Ignoring process request for pid={Pid} because the observed process identity changed (expected={ExpectedIdentity}, actual={ActualIdentity}).",
                    request.ProcessId,
                    request.ExpectedProcessName,
                    snapshot.ProcessName);
                return;
            }

            ObserveSnapshot(configuration, configurationVersion, snapshot, stoppingToken);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Skipping process event for pid={Pid} because the process could not be opened.",
                request.ProcessId);
        }
    }

    private void ObserveSnapshot(
        ModServiceConfiguration configuration,
        long configurationVersion,
        ProcessSnapshot snapshot,
        CancellationToken stoppingToken)
    {
        var key = CreateProcessKey(snapshot);
        var now = DateTimeOffset.UtcNow;
        if (_state.ShouldSkip(key, now))
        {
            return;
        }

        var resources = new ProcessObservationResources
        {
            Assets = syncService.LoadCurrentAssets()
        };

        var plan = resolver.Resolve(configuration, snapshot, resources.Assets);
        if (plan is null)
        {
            _state.MarkObserved(key);
            return;
        }

        if (plan.ModulePaths.Count == 0)
        {
            logger.LogWarning(
                "Matched process {ProcessName} (pid={Pid}) with rule {RuleName}, but no module paths are available yet.",
                snapshot.ProcessName,
                snapshot.ProcessId,
                plan.RuleName);
            ScheduleTrackedRetry(snapshot, configurationVersion, now, stoppingToken);
            return;
        }

        resources.ExecutorPath = TryResolveExecutorPath(configuration);
        if (string.IsNullOrWhiteSpace(resources.ExecutorPath))
        {
            logger.LogWarning(
                "Matched process {ProcessName} (pid={Pid}) with rule {RuleName}, but the executor is not available yet.",
                snapshot.ProcessName,
                snapshot.ProcessId,
                plan.RuleName);
            ScheduleTrackedRetry(snapshot, configurationVersion, now, stoppingToken);
            return;
        }

        if (TryExecute(snapshot, plan, resources.ExecutorPath))
        {
            _state.MarkObserved(key);

            var summary = $"Processed {snapshot.ProcessName} (pid={snapshot.ProcessId}) with rule {plan.RuleName}.";
            runtimeState.MarkActivation(summary);
            if (preferencesStore.AreProcessNotificationsEnabled())
            {
                notificationQueue.Enqueue("ModService", summary);
            }
        }
        else
        {
            ScheduleTrackedRetry(snapshot, configurationVersion, now, stoppingToken);
            runtimeState.MarkActivation(
                $"Failed to process {snapshot.ProcessName} (pid={snapshot.ProcessId}) with rule {plan.RuleName}; retry scheduled.");
        }
    }

    private void ScheduleWarmupRetry(
        ProcessObservationRequest request,
        long configurationVersion,
        string? failureReason,
        CancellationToken stoppingToken)
    {
        var nextAttempt = request.Attempt + 1;
        var delay = request.Attempt < MaxWarmupAttempts
            ? WarmupRetryDelay
            : configurationStore.GetProcessScanDelay();

        logger.LogDebug(
            "Process event for pid={Pid} is not ready for capture on attempt {Attempt}; retrying in {DelayMs} ms. Reason: {Reason}",
            request.ProcessId,
            nextAttempt,
            (int)delay.TotalMilliseconds,
            failureReason ?? "no reason provided");

        _ = RequeueProcessAsync(
            ProcessObservationRequest.ProcessRetry(
                request.ProcessId,
                request.ExpectedProcessName,
                request.ExpectedExePath,
                nextAttempt,
                configurationVersion),
            delay,
            stoppingToken);
    }

    private void ScheduleTrackedRetry(
        ProcessSnapshot snapshot,
        long configurationVersion,
        DateTimeOffset now,
        CancellationToken stoppingToken)
    {
        var delay = configurationStore.GetProcessScanDelay();
        _state.ScheduleRetry(CreateProcessKey(snapshot), now, delay);

        _ = RequeueProcessAsync(
            ProcessObservationRequest.ProcessRetry(
                snapshot.ProcessId,
                snapshot.ProcessName,
                snapshot.ExePath,
                attempt: MaxWarmupAttempts + 1,
                configurationVersion),
            delay,
            stoppingToken);
    }

    private async Task RequeueProcessAsync(
        ProcessObservationRequest request,
        TimeSpan delay,
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            _requests.Writer.TryWrite(request);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void RefreshMonitoringStatus()
    {
        if (!configurationStore.TryGetCurrent(out var configuration))
        {
            runtimeState.SetProcessMonitoringStatus(
                enabled: false,
                summary: "No valid configuration is available.");
            return;
        }

        runtimeState.SetProcessMonitoringStatus(
            configuration.ProcessMonitoring.Enabled,
            configuration.ProcessMonitoring.Enabled
                ? "Watching new process start/stop events only."
                : "Process monitoring is disabled.");
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
                TimeoutMs = 30_000
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

    private static bool MatchesObservedProcess(ProcessObservationRequest request, ProcessSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedExePath))
        {
            return string.Equals(request.ExpectedExePath, snapshot.ExePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedProcessName) &&
            !string.Equals(request.ExpectedProcessName, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool TryCreateSnapshot(Process process, out ProcessSnapshot snapshot, out string? failureReason)
    {
        snapshot = null!;
        failureReason = null;

        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                failureReason = "The process executable path is not available yet.";
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

    private sealed class ProcessObservationResources
    {
        public IReadOnlyList<SourceAsset>? Assets { get; init; }

        public string? ExecutorPath { get; set; }
    }

    private enum ProcessObservationKind
    {
        None,
        ProcessStarted,
        ProcessExited
    }

    private readonly record struct ProcessObservationRequest(
        ProcessObservationKind Kind,
        int ProcessId,
        string? ExpectedProcessName,
        string? ExpectedExePath,
        int Attempt,
        long? ConfigurationVersion)
    {
        public static ProcessObservationRequest ProcessStarted(
            int processId,
            string? processName)
            => new(ProcessObservationKind.ProcessStarted, processId, processName, null, 0, null);

        public static ProcessObservationRequest ProcessRetry(
            int processId,
            string? processName,
            string? expectedExePath,
            int attempt,
            long configurationVersion)
            => new(ProcessObservationKind.ProcessStarted, processId, processName, expectedExePath, attempt, configurationVersion);

        public static ProcessObservationRequest ProcessExited(int processId)
            => new(ProcessObservationKind.ProcessExited, processId, null, null, 0, null);
    }
}
