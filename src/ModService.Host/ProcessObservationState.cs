using System.Globalization;

namespace ModService.Host;

internal sealed class ProcessObservationState
{
    private readonly HashSet<string> _observedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private long _configurationVersion = -1;

    public bool ResetForConfiguration(long version)
    {
        if (version == _configurationVersion)
        {
            return false;
        }

        _observedProcesses.Clear();
        _retryAfter.Clear();
        _configurationVersion = version;
        return true;
    }

    public bool ShouldSkip(string key, DateTimeOffset now)
    {
        if (_observedProcesses.Contains(key))
        {
            return true;
        }

        return _retryAfter.TryGetValue(key, out var retryAfter) &&
               retryAfter > now;
    }

    public void MarkObserved(string key)
    {
        _observedProcesses.Add(key);
        _retryAfter.Remove(key);
    }

    public void ScheduleRetry(string key, DateTimeOffset now, TimeSpan delay)
        => _retryAfter[key] = now + delay;

    public void RemoveMissing(ISet<string> currentKeys)
    {
        _observedProcesses.RemoveWhere(key => !currentKeys.Contains(key));
        foreach (var key in _retryAfter.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _retryAfter.Remove(key);
        }
    }

    public void RemoveProcess(int processId)
    {
        var prefix = processId.ToString(CultureInfo.InvariantCulture) + "|";
        _observedProcesses.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        foreach (var key in _retryAfter.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _retryAfter.Remove(key);
        }
    }
}
