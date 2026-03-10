using Microsoft.Extensions.Options;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Host;

public sealed class EffectiveConfigurationStore : IDisposable
{
    private static readonly TimeSpan InvalidConfigurationRetryDelay = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly IDisposable? _subscription;
    private readonly ILogger<EffectiveConfigurationStore> _logger;

    private ModServiceConfiguration? _current;

    public EffectiveConfigurationStore(
        IOptionsMonitor<ModServiceConfiguration> optionsMonitor,
        ILogger<EffectiveConfigurationStore> logger)
    {
        _logger = logger;
        ApplyCandidate("startup", optionsMonitor.CurrentValue);
        _subscription = optionsMonitor.OnChange((configuration, _) => ApplyCandidate("reload", configuration));
    }

    public bool TryGetCurrent(out ModServiceConfiguration configuration)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                configuration = null!;
                return false;
            }

            configuration = _current;
            return true;
        }
    }

    public TimeSpan GetSyncDelay()
    {
        if (!TryGetCurrent(out var configuration))
        {
            return InvalidConfigurationRetryDelay;
        }

        return PollingDelayCalculator.ComputeNextDelay(configuration.Polling);
    }

    public TimeSpan GetProcessScanDelay()
    {
        if (!TryGetCurrent(out var configuration))
        {
            return InvalidConfigurationRetryDelay;
        }

        return TimeSpan.FromSeconds(Math.Max(1, configuration.ProcessMonitoring.ScanIntervalSeconds));
    }

    public void Dispose()
        => _subscription?.Dispose();

    private void ApplyCandidate(string reason, ModServiceConfiguration candidate)
    {
        var errors = ConfigurationValidator.Validate(candidate);
        if (errors.Count > 0)
        {
            if (TryGetCurrent(out _))
            {
                _logger.LogWarning(
                    "Rejected invalid configuration {Reason} with {ErrorCount} validation errors; retaining last-known-good config: {Errors}",
                    reason,
                    errors.Count,
                    string.Join(" | ", errors));
            }
            else
            {
                _logger.LogWarning(
                    "Configuration {Reason} is invalid with {ErrorCount} validation errors and no last-known-good config is available: {Errors}",
                    reason,
                    errors.Count,
                    string.Join(" | ", errors));
            }

            return;
        }

        lock (_gate)
        {
            _current = candidate;
        }

        _logger.LogInformation(
            "Accepted configuration {Reason}: executor {ExecutorSource}/{ExecutorAsset}, {SourceCount} sources, {RuleCount} rules, polling {IntervalSeconds}s+{JitterSeconds}s jitter.",
            reason,
            candidate.Executor.Source,
            candidate.Executor.Asset,
            candidate.Sources.Count,
            candidate.Rules.Count,
            candidate.Polling.IntervalSeconds,
            candidate.Polling.JitterSeconds);
    }
}
