using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Tests;

public sealed class PollingDelayCalculatorTests
{
    [Fact]
    public void ComputeNextDelay_UsesBaseInterval_WhenJitterIsZero()
    {
        var delay = PollingDelayCalculator.ComputeNextDelay(new PollingConfiguration
        {
            IntervalSeconds = 120,
            JitterSeconds = 0
        }, new Random(123));

        Assert.Equal(TimeSpan.FromSeconds(120), delay);
    }

    [Fact]
    public void ComputeNextDelay_AddsBoundedJitter()
    {
        var delay = PollingDelayCalculator.ComputeNextDelay(new PollingConfiguration
        {
            IntervalSeconds = 60,
            JitterSeconds = 15
        }, new Random(1));

        Assert.InRange(delay.TotalSeconds, 60, 75);
    }

    [Fact]
    public void Validate_RejectsInvalidPollingValues()
    {
        var configuration = new ModServiceConfiguration
        {
            Polling = new PollingConfiguration
            {
                IntervalSeconds = 0,
                JitterSeconds = -1
            },
            Executor = new ExecutorConfiguration { Source = "repo", Asset = "NativeExecutor.dll" },
            Sources = [new SourceConfiguration { Id = "repo", Repo = "owner/repo", Tag = "stable" }]
        };

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("intervalSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("jitterSeconds", StringComparison.Ordinal));
    }
}
