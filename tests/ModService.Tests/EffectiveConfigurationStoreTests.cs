using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModService.Core.Configuration;
using ModService.Host;

namespace ModService.Tests;

public sealed class EffectiveConfigurationStoreTests
{
    [Fact]
    public void RetainsLastKnownGoodConfig_WhenReloadBecomesInvalid()
    {
        var initial = CreateValidConfiguration(intervalSeconds: 30);
        var monitor = new TestOptionsMonitor(initial);

        using var store = new EffectiveConfigurationStore(monitor, NullLogger<EffectiveConfigurationStore>.Instance);
        Assert.True(store.TryGetCurrent(out var effectiveBeforeReload));
        Assert.Equal(30, effectiveBeforeReload.Polling.IntervalSeconds);

        var invalid = CreateValidConfiguration(intervalSeconds: 0);
        monitor.Set(invalid);

        Assert.True(store.TryGetCurrent(out var effectiveAfterReload));
        Assert.Equal(30, effectiveAfterReload.Polling.IntervalSeconds);
    }

    [Fact]
    public void AcceptsFirstValidReload_WhenStartupConfigurationIsInvalid()
    {
        var invalid = CreateValidConfiguration(intervalSeconds: 0);
        var valid = CreateValidConfiguration(intervalSeconds: 45);
        var monitor = new TestOptionsMonitor(invalid);

        using var store = new EffectiveConfigurationStore(monitor, NullLogger<EffectiveConfigurationStore>.Instance);
        Assert.False(store.TryGetCurrent(out _));

        monitor.Set(valid);

        Assert.True(store.TryGetCurrent(out var effective));
        Assert.Equal(45, effective.Polling.IntervalSeconds);
    }

    private static ModServiceConfiguration CreateValidConfiguration(int intervalSeconds)
    {
        return new ModServiceConfiguration
        {
            Polling = new PollingConfiguration
            {
                IntervalSeconds = intervalSeconds,
                JitterSeconds = 0
            },
            Executor = new ExecutorConfiguration
            {
                Source = "repo",
                Asset = "NativeExecutor.dll"
            },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "repo",
                    Repo = "owner/repo",
                    Tag = "latest"
                }
            ]
        };
    }

    private sealed class TestOptionsMonitor(ModServiceConfiguration currentValue) : IOptionsMonitor<ModServiceConfiguration>
    {
        private ModServiceConfiguration _currentValue = currentValue;
        private event Action<ModServiceConfiguration, string?>? Changed;

        public ModServiceConfiguration CurrentValue => _currentValue;

        public ModServiceConfiguration Get(string? name)
            => _currentValue;

        public IDisposable OnChange(Action<ModServiceConfiguration, string?> listener)
        {
            Changed += listener;
            return new Subscription(() => Changed -= listener);
        }

        public void Set(ModServiceConfiguration value)
        {
            _currentValue = value;
            Changed?.Invoke(value, string.Empty);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose()
                => dispose();
        }
    }
}
