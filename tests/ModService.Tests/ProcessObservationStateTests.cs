using ModService.Host;

namespace ModService.Tests;

public sealed class ProcessObservationStateTests
{
    [Fact]
    public void ShouldSkip_ReturnsTrue_ForObservedProcess()
    {
        var state = new ProcessObservationState();
        state.ResetForConfiguration(1);
        state.MarkObserved("42|100");

        var shouldSkip = state.ShouldSkip("42|100", DateTimeOffset.UtcNow);

        Assert.True(shouldSkip);
    }

    [Fact]
    public void ShouldSkip_ExpiresRetryWindow()
    {
        var state = new ProcessObservationState();
        var now = DateTimeOffset.UtcNow;

        state.ResetForConfiguration(1);
        state.ScheduleRetry("42|100", now, TimeSpan.FromSeconds(5));

        Assert.True(state.ShouldSkip("42|100", now.AddSeconds(4)));
        Assert.False(state.ShouldSkip("42|100", now.AddSeconds(6)));
    }

    [Fact]
    public void RemoveProcess_ClearsObservedAndRetryEntriesForMatchingPid()
    {
        var state = new ProcessObservationState();
        var now = DateTimeOffset.UtcNow;

        state.ResetForConfiguration(1);
        state.MarkObserved("42|100");
        state.ScheduleRetry("42|101", now, TimeSpan.FromSeconds(5));
        state.MarkObserved("77|200");

        state.RemoveProcess(42);

        Assert.False(state.ShouldSkip("42|100", now));
        Assert.False(state.ShouldSkip("42|101", now));
        Assert.True(state.ShouldSkip("77|200", now));
    }

    [Fact]
    public void ResetForConfiguration_ClearsExistingState()
    {
        var state = new ProcessObservationState();
        var now = DateTimeOffset.UtcNow;

        state.ResetForConfiguration(1);
        state.MarkObserved("42|100");
        state.ScheduleRetry("42|101", now, TimeSpan.FromSeconds(5));

        var changed = state.ResetForConfiguration(2);

        Assert.True(changed);
        Assert.False(state.ShouldSkip("42|100", now));
        Assert.False(state.ShouldSkip("42|101", now));
    }
}
