namespace ModService.Core.Updates;

public sealed class GitHubReleaseAsset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public interface IGitHubReleaseClient
{
    Task<IReadOnlyList<GitHubReleaseAsset>> GetReleaseAssetsAsync(Configuration.SourceConfiguration source, CancellationToken cancellationToken);

    Task<string> DownloadAssetAsync(
        Configuration.SourceConfiguration source,
        GitHubReleaseAsset asset,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
