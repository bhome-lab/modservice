using System.Net;

namespace ModService.Core.Updates;

public sealed class GitHubRateLimitException : InvalidOperationException
{
    public GitHubRateLimitException(
        string message,
        HttpStatusCode statusCode,
        int? limit,
        int? remaining,
        DateTimeOffset? resetAtUtc,
        TimeSpan? retryAfter,
        string? scope,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Limit = limit;
        Remaining = remaining;
        ResetAtUtc = resetAtUtc;
        RetryAfter = retryAfter;
        Scope = scope;
    }

    public HttpStatusCode StatusCode { get; }

    public int? Limit { get; }

    public int? Remaining { get; }

    public DateTimeOffset? ResetAtUtc { get; }

    public TimeSpan? RetryAfter { get; }

    public string? Scope { get; }

    public DateTimeOffset GetBackoffUntilUtc(DateTimeOffset nowUtc, TimeSpan fallbackDelay)
    {
        if (RetryAfter is { } retryAfter)
        {
            return nowUtc + NormalizeDelay(retryAfter, fallbackDelay);
        }

        if (ResetAtUtc is { } resetAtUtc)
        {
            return resetAtUtc > nowUtc ? resetAtUtc : nowUtc + fallbackDelay;
        }

        return nowUtc + fallbackDelay;
    }

    private static TimeSpan NormalizeDelay(TimeSpan value, TimeSpan fallbackDelay)
        => value <= TimeSpan.Zero ? fallbackDelay : value;
}
