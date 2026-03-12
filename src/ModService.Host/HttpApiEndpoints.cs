using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Updates;

namespace ModService.Host;

public static class HttpApiEndpoints
{
    public static IEndpointRouteBuilder MapModServiceApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/status", (
            EffectiveConfigurationStore configurationStore,
            RuntimeStateStore runtimeState,
            SourceSyncService syncService) =>
        {
            var configurationStatus = configurationStore.GetStatus();
            var runtime = runtimeState.GetSnapshot();
            var sources = configurationStore.TryGetCurrent(out var configuration)
                ? BuildSourceItems(configuration, syncService)
                : [];

            return Results.Json(new StatusResponse(
                new ServiceResponse(runtime.StartedAtUtc),
                new ConfigurationResponse(
                    configurationStatus.Version,
                    configurationStatus.HasConfiguration,
                    configurationStatus.UsingLastKnownGoodConfiguration,
                    configurationStatus.ValidationErrors),
                new RefreshResponse(
                    runtime.RefreshInProgress,
                    runtime.QueuedRefreshCount,
                    runtime.LastRefreshReason,
                    runtime.LastRefreshStartedAtUtc,
                    runtime.LastRefreshCompletedAtUtc,
                    runtime.LastRefreshSummary,
                    runtime.LastRefreshError),
                BuildGitHubResponse(runtime.GitHub),
                new ExecutorResponse(runtime.ExecutorPath),
                new CleanupResponse(
                    runtime.Cleanup.StaleFileCount,
                    runtime.Cleanup.LockedFileCount,
                    runtime.Cleanup.Deleted),
                sources));
        });

        api.MapGet("/sources", (string? glob, EffectiveConfigurationStore configurationStore, SourceSyncService syncService) =>
        {
            if (!configurationStore.TryGetCurrent(out var configuration))
            {
                return InvalidConfiguration();
            }

            var sourceGlob = NormalizeGlob(glob);
            var items = BuildSourceItems(configuration, syncService)
                .Where(item => GlobPattern.IsMatch(sourceGlob, item.SourceId))
                .ToArray();

            return Results.Json(new SourceListResponse(items));
        });

        api.MapGet("/sources/{sourceId}/dlls", (string sourceId, string? glob, EffectiveConfigurationStore configurationStore, SourceSyncService syncService) =>
        {
            if (!configurationStore.TryGetCurrent(out var configuration))
            {
                return InvalidConfiguration();
            }

            var source = configuration.Sources.FirstOrDefault(item => string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return Error(StatusCodes.Status404NotFound, "source_not_found", $"Source '{sourceId}' was not found.");
            }

            var items = BuildDllItems([source], syncService, NormalizeGlob(glob));
            return Results.Json(new SourceDllListResponse(source.Id, items));
        });

        api.MapGet("/dlls", (string? sourceGlob, string? glob, EffectiveConfigurationStore configurationStore, SourceSyncService syncService) =>
        {
            if (!configurationStore.TryGetCurrent(out var configuration))
            {
                return InvalidConfiguration();
            }

            var normalizedSourceGlob = NormalizeGlob(sourceGlob);
            var normalizedDllGlob = NormalizeGlob(glob);
            var filteredSources = configuration.Sources
                .Where(source => GlobPattern.IsMatch(normalizedSourceGlob, source.Id))
                .OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var items = BuildDllItems(filteredSources, syncService, normalizedDllGlob);
            return Results.Json(new DllListResponse(items));
        });

        return endpoints;
    }

    private static IResult InvalidConfiguration()
        => Error(StatusCodes.Status503ServiceUnavailable, "invalid_configuration", "No effective configuration is available.");

    private static IResult Error(int statusCode, string code, string message)
        => Results.Json(new ErrorEnvelope(new ErrorResponse(code, message, null)), statusCode: statusCode);

    private static string NormalizeGlob(string? value)
        => string.IsNullOrWhiteSpace(value) ? "*" : value.Trim().Replace('\\', '/');

    private static SourceItemResponse[] BuildSourceItems(ModServiceConfiguration configuration, SourceSyncService syncService)
    {
        return configuration.Sources
            .OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .Select(source =>
            {
                var manifest = syncService.LoadManifest(source.Id);
                return new SourceItemResponse(
                    source.Id,
                    source.Repo,
                    source.Tag,
                    manifest?.SyncedAtUtc,
                    manifest?.CurrentAssets.Count ?? 0,
                    manifest is null ? "No manifest available yet." : "Ready");
            })
            .ToArray();
    }

    private static DllItemResponse[] BuildDllItems(IEnumerable<SourceConfiguration> sources, SourceSyncService syncService, string assetGlob)
    {
        return sources
            .SelectMany(source =>
            {
                var manifest = syncService.LoadManifest(source.Id);
                if (manifest is null)
                {
                    return [];
                }

                return manifest.CurrentAssets
                    .Where(asset => GlobPattern.IsMatch(assetGlob, NormalizeAssetName(asset.AssetName)))
                    .Select(asset => new DllItemResponse(
                        source.Id,
                        asset.AssetName,
                        asset.FullPath,
                        asset.Sha256,
                        asset.Size,
                        asset.UpdatedAtUtc));
            })
            .OrderBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static GitHubResponse BuildGitHubResponse(GitHubSyncStatusSnapshot status)
    {
        var rateLimit = status.RateLimit;
        return new GitHubResponse(
            status.State,
            rateLimit is null
                ? null
                : new GitHubRateLimitResponse(
                    rateLimit.Scope,
                    rateLimit.Limit,
                    rateLimit.Remaining,
                    rateLimit.ResetAtUtc,
                    rateLimit.BackoffUntilUtc,
                    rateLimit.BackoffUntilUtc is { } backoffUntilUtc
                        ? Math.Max(0, (int)Math.Ceiling((backoffUntilUtc - DateTimeOffset.UtcNow).TotalSeconds))
                        : null,
                    rateLimit.Message),
            status.Error);
    }

    private static string NormalizeAssetName(string value)
        => value.Replace('\\', '/');

    public sealed record StatusResponse(
        ServiceResponse Service,
        ConfigurationResponse Configuration,
        RefreshResponse Refresh,
        [property: JsonPropertyName("github")]
        GitHubResponse GitHub,
        ExecutorResponse Executor,
        CleanupResponse Cleanup,
        IReadOnlyList<SourceItemResponse> Sources);

    public sealed record ServiceResponse(DateTimeOffset StartedAtUtc);

    public sealed record ConfigurationResponse(
        long Version,
        bool HasConfiguration,
        bool UsingLastKnownGoodConfiguration,
        IReadOnlyList<string> ValidationErrors);

    public sealed record RefreshResponse(
        bool InProgress,
        int QueuedCount,
        string LastReason,
        DateTimeOffset? LastStartedAtUtc,
        DateTimeOffset? LastCompletedAtUtc,
        string LastSummary,
        string? LastError);

    public sealed record GitHubResponse(
        string State,
        GitHubRateLimitResponse? RateLimit,
        string? Error);

    public sealed record GitHubRateLimitResponse(
        string? Scope,
        int? Limit,
        int? Remaining,
        DateTimeOffset? ResetAtUtc,
        DateTimeOffset? BackoffUntilUtc,
        int? RetryAfterSeconds,
        string? Message);

    public sealed record ExecutorResponse(string? Path);

    public sealed record CleanupResponse(int StaleFileCount, int LockedFileCount, bool Deleted);

    public sealed record SourceItemResponse(
        string SourceId,
        string Repo,
        string Tag,
        DateTimeOffset? SyncedAtUtc,
        int AssetCount,
        string Status);

    public sealed record SourceListResponse(IReadOnlyList<SourceItemResponse> Items);

    public sealed record SourceDllListResponse(string SourceId, IReadOnlyList<DllItemResponse> Items);

    public sealed record DllListResponse(IReadOnlyList<DllItemResponse> Items);

    public sealed record DllItemResponse(
        string SourceId,
        string AssetName,
        string FullPath,
        string Sha256,
        long Size,
        DateTimeOffset UpdatedAtUtc);

    public sealed record ErrorEnvelope(ErrorResponse Error);

    public sealed record ErrorResponse(string Code, string Message, object? Details);
}
