namespace ModService.Core.Updates;

public sealed class StorageLayout(string rootDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public string SourcesDirectory => Path.Combine(RootDirectory, "sources");

    public string ManifestsDirectory => Path.Combine(RootDirectory, "manifests");

    public string StagingDirectory => Path.Combine(RootDirectory, "staging");

    public string GetManifestPath(string sourceId) => Path.Combine(ManifestsDirectory, $"{sourceId}.json");

    public string GetSourceDirectory(string sourceId)
        => Path.Combine(SourcesDirectory, sourceId);

    public string GetDirectDirectory(string sourceId)
        => Path.Combine(GetSourceDirectory(sourceId), "direct");

    public string GetDirectRevisionRoot(string sourceId, string revisionHash)
        => Path.Combine(GetDirectDirectory(sourceId), revisionHash);

    public string GetArchiveDirectory(string sourceId)
        => Path.Combine(GetSourceDirectory(sourceId), "archives");

    public string GetArchiveAssetDirectory(string sourceId, string archiveAssetName)
        => Path.Combine(GetArchiveDirectory(sourceId), archiveAssetName);

    public string GetArchiveRevisionRoot(string sourceId, string archiveAssetName, string archiveSha256)
        => Path.Combine(GetArchiveAssetDirectory(sourceId, archiveAssetName), archiveSha256);

    public string GetSourceStagingDirectory(string sourceId)
        => Path.Combine(StagingDirectory, sourceId, Guid.NewGuid().ToString("N"));
}
