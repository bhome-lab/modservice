namespace ModService.Host;

public sealed class RuntimeStateStore
{
    private const int MaxEventCount = 50;

    private readonly object _gate = new();
    private RuntimeSnapshot _snapshot = new();

    public RuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot with
            {
                Sources = _snapshot.Sources.ToArray(),
                RecentEvents = _snapshot.RecentEvents.ToArray()
            };
        }
    }

    public void SetQueuedRefreshCount(int queuedRefreshCount)
    {
        Update(snapshot => snapshot with
        {
            QueuedRefreshCount = Math.Max(0, queuedRefreshCount)
        });
    }

    public void MarkRefreshQueued(string reason, int queuedRefreshCount)
    {
        Update(snapshot => AddEvent(
            snapshot with
            {
                QueuedRefreshCount = Math.Max(0, queuedRefreshCount)
            },
            $"Refresh queued ({reason})."));
    }

    public void MarkRefreshStarted(string reason, int queuedRefreshCount)
    {
        Update(snapshot => AddEvent(
            snapshot with
            {
                RefreshInProgress = true,
                QueuedRefreshCount = Math.Max(0, queuedRefreshCount),
                LastRefreshReason = reason,
                LastRefreshStartedAtUtc = DateTimeOffset.UtcNow,
                LastRefreshError = null
            },
            $"Refresh started ({reason})."));
    }

    public void SetGitHubReady()
    {
        Update(snapshot => snapshot with
        {
            GitHub = new GitHubSyncStatusSnapshot
            {
                State = "ready"
            }
        });
    }

    public void SetGitHubRateLimited(
        int? limit,
        int? remaining,
        DateTimeOffset? resetAtUtc,
        DateTimeOffset backoffUntilUtc,
        string? scope,
        string? message)
    {
        Update(snapshot => AddEvent(
            snapshot with
            {
                GitHub = new GitHubSyncStatusSnapshot
                {
                    State = "rate_limited",
                    RateLimit = new GitHubRateLimitStatusSnapshot
                    {
                        Limit = limit,
                        Remaining = remaining,
                        ResetAtUtc = resetAtUtc,
                        BackoffUntilUtc = backoffUntilUtc,
                        Scope = scope,
                        Message = string.IsNullOrWhiteSpace(message)
                            ? "GitHub API rate limit exceeded."
                            : message
                    }
                }
            },
            $"GitHub rate limit active until {backoffUntilUtc.ToLocalTime():G}."));
    }

    public void SetGitHubError(string message)
    {
        Update(snapshot => snapshot with
        {
            GitHub = new GitHubSyncStatusSnapshot
            {
                State = "error",
                Error = message
            }
        });
    }

    public void MarkRefreshCompleted(
        string reason,
        bool success,
        string summary,
        string? error,
        string? executorPath,
        IReadOnlyList<SourceStatusSnapshot> sources,
        CleanupStatusSnapshot cleanup)
    {
        Update(snapshot => AddEvent(
            snapshot with
            {
                RefreshInProgress = false,
                LastRefreshReason = reason,
                LastRefreshCompletedAtUtc = DateTimeOffset.UtcNow,
                LastRefreshSummary = summary,
                LastRefreshError = error,
                ExecutorPath = executorPath,
                Sources = sources.ToArray(),
                Cleanup = cleanup
            },
            success ? summary : $"Refresh failed ({reason}): {error ?? summary}"));
    }

    public void MarkProcessScan(
        bool enabled,
        int accessibleProcessCount,
        int inaccessibleProcessCount,
        string? inaccessibleSummary)
    {
        var summary = !enabled
            ? "Process monitoring is disabled."
            : inaccessibleProcessCount == 0
                ? $"Scanned {accessibleProcessCount} processes."
                : $"Scanned {accessibleProcessCount} processes; skipped {inaccessibleProcessCount} inaccessible process entries.";

        if (!string.IsNullOrWhiteSpace(inaccessibleSummary))
        {
            summary = $"{summary} {inaccessibleSummary}";
        }

        Update(snapshot => snapshot with
        {
            ProcessMonitoringEnabled = enabled,
            VisibleProcessCount = Math.Max(0, accessibleProcessCount),
            InaccessibleProcessCount = Math.Max(0, inaccessibleProcessCount),
            LastProcessScanAtUtc = DateTimeOffset.UtcNow,
            LastProcessScanSummary = summary
        });
    }

    public void MarkActivation(string summary)
    {
        Update(snapshot => AddEvent(
            snapshot with
            {
                LastActivationAtUtc = DateTimeOffset.UtcNow,
                LastActivationSummary = summary
            },
            summary));
    }

    private void Update(Func<RuntimeSnapshot, RuntimeSnapshot> updater)
    {
        lock (_gate)
        {
            _snapshot = updater(_snapshot);
        }
    }

    private static RuntimeSnapshot AddEvent(RuntimeSnapshot snapshot, string message)
    {
        var events = new List<string>(snapshot.RecentEvents.Count + 1)
        {
            $"{DateTimeOffset.Now:HH:mm:ss} {message}"
        };
        events.AddRange(snapshot.RecentEvents);

        if (events.Count > MaxEventCount)
        {
            events.RemoveRange(MaxEventCount, events.Count - MaxEventCount);
        }

        return snapshot with
        {
            RecentEvents = events.ToArray()
        };
    }
}

public sealed record RuntimeSnapshot
{
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool RefreshInProgress { get; init; }

    public int QueuedRefreshCount { get; init; }

    public string LastRefreshReason { get; init; } = "startup";

    public DateTimeOffset? LastRefreshStartedAtUtc { get; init; }

    public DateTimeOffset? LastRefreshCompletedAtUtc { get; init; }

    public string LastRefreshSummary { get; init; } = "Waiting for first refresh.";

    public string? LastRefreshError { get; init; }

    public string? ExecutorPath { get; init; }

    public IReadOnlyList<SourceStatusSnapshot> Sources { get; init; } = [];

    public GitHubSyncStatusSnapshot GitHub { get; init; } = new();

    public CleanupStatusSnapshot Cleanup { get; init; } = new();

    public bool ProcessMonitoringEnabled { get; init; }

    public int VisibleProcessCount { get; init; }

    public int InaccessibleProcessCount { get; init; }

    public DateTimeOffset? LastProcessScanAtUtc { get; init; }

    public string LastProcessScanSummary { get; init; } = "No process scan yet.";

    public DateTimeOffset? LastActivationAtUtc { get; init; }

    public string LastActivationSummary { get; init; } = "No process activations yet.";

    public IReadOnlyList<string> RecentEvents { get; init; } = [];
}

public sealed record SourceStatusSnapshot
{
    public string SourceId { get; init; } = string.Empty;

    public DateTimeOffset? SyncedAtUtc { get; init; }

    public int AssetCount { get; init; }

    public string Status { get; init; } = string.Empty;
}

public sealed record CleanupStatusSnapshot
{
    public int StaleFileCount { get; init; }

    public int LockedFileCount { get; init; }

    public bool Deleted { get; init; }
}

public sealed record GitHubSyncStatusSnapshot
{
    public string State { get; init; } = "ready";

    public GitHubRateLimitStatusSnapshot? RateLimit { get; init; }

    public string? Error { get; init; }
}

public sealed record GitHubRateLimitStatusSnapshot
{
    public int? Limit { get; init; }

    public int? Remaining { get; init; }

    public DateTimeOffset? ResetAtUtc { get; init; }

    public DateTimeOffset? BackoffUntilUtc { get; init; }

    public string? Scope { get; init; }

    public string? Message { get; init; }
}
