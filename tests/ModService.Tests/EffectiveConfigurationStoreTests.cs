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

    [Fact]
    public void Status_TracksValidationErrors_AndVersion()
    {
        var initial = CreateValidConfiguration(intervalSeconds: 30);
        var monitor = new TestOptionsMonitor(initial);

        using var store = new EffectiveConfigurationStore(monitor, NullLogger<EffectiveConfigurationStore>.Instance);
        var initialStatus = store.GetStatus();
        Assert.True(initialStatus.HasConfiguration);
        Assert.False(initialStatus.UsingLastKnownGoodConfiguration);
        Assert.Empty(initialStatus.ValidationErrors);
        Assert.Equal(1, initialStatus.Version);

        monitor.Set(CreateValidConfiguration(intervalSeconds: 0));

        var invalidStatus = store.GetStatus();
        Assert.True(invalidStatus.HasConfiguration);
        Assert.True(invalidStatus.UsingLastKnownGoodConfiguration);
        Assert.NotEmpty(invalidStatus.ValidationErrors);
        Assert.Equal(1, invalidStatus.Version);

        monitor.Set(CreateValidConfiguration(intervalSeconds: 60));

        var reloadedStatus = store.GetStatus();
        Assert.True(reloadedStatus.HasConfiguration);
        Assert.False(reloadedStatus.UsingLastKnownGoodConfiguration);
        Assert.Empty(reloadedStatus.ValidationErrors);
        Assert.Equal(2, reloadedStatus.Version);
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
