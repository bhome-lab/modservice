using ModService.Core.Configuration;

namespace ModService.Core.Updates;

public static class PollingDelayCalculator
{
    public static TimeSpan ComputeNextDelay(PollingConfiguration polling, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(polling);

        var interval = Math.Max(1, polling.IntervalSeconds);
        var jitter = Math.Max(0, polling.JitterSeconds);
        if (jitter == 0)
        {
            return TimeSpan.FromSeconds(interval);
        }

        var generator = random ?? Random.Shared;
        var extraSeconds = generator.Next(0, jitter + 1);
        return TimeSpan.FromSeconds(interval + extraSeconds);
    }
}
