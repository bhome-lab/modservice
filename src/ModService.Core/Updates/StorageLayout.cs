namespace ModService.Core.Updates;

public sealed class StorageLayout(string rootDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public string SourcesDirectory => Path.Combine(RootDirectory, "sources");

    public string ManifestsDirectory => Path.Combine(RootDirectory, "manifests");

    public string StagingDirectory => Path.Combine(RootDirectory, "staging");

    public string GetManifestPath(string sourceId) => Path.Combine(ManifestsDirectory, $"{sourceId}.json");

    public string GetAssetDirectory(string sourceId, string assetName, string sha256)
        => Path.Combine(SourcesDirectory, sourceId, assetName, sha256);

    public string GetAssetPath(string sourceId, string assetName, string sha256)
        => Path.Combine(GetAssetDirectory(sourceId, assetName, sha256), assetName);

    public string GetSourceStagingDirectory(string sourceId)
        => Path.Combine(StagingDirectory, sourceId, Guid.NewGuid().ToString("N"));
}
