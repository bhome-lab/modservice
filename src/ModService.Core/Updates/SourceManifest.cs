namespace ModService.Core.Updates;

public sealed class SourceManifest
{
    public required string SourceId { get; init; }

    public required string Repo { get; init; }

    public required string Tag { get; init; }

    public DateTimeOffset SyncedAtUtc { get; init; }

    public List<CurrentAssetRecord> CurrentAssets { get; init; } = [];
}

public sealed class CurrentAssetRecord
{
    public required string Id { get; init; }

    public required string AssetName { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string Sha256 { get; init; }

    public required string FullPath { get; init; }
}

public sealed class SourceSyncResult
{
    public required SourceManifest Manifest { get; init; }

    public required IReadOnlyList<string> DownloadedAssets { get; init; }
}

public sealed class CleanupResult
{
    public required int StaleFileCount { get; init; }

    public required bool Deleted { get; init; }

    public required IReadOnlyList<string> LockedFiles { get; init; }

    public required IReadOnlyList<string> StaleFiles { get; init; }
}
