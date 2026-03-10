using System.Security.Cryptography;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Execution;

namespace ModService.Core.Updates;

public sealed class SourceSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly StorageLayout _layout;
    private readonly IGitHubReleaseClient _client;

    public SourceSyncService(StorageLayout layout, IGitHubReleaseClient client)
    {
        _layout = layout;
        _client = client;
    }

    public async Task<IReadOnlyList<SourceSyncResult>> SyncAsync(ModServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var results = new List<SourceSyncResult>();
        foreach (var source in configuration.Sources)
        {
            results.Add(await SyncSourceAsync(source, cancellationToken));
        }

        return results;
    }

    public async Task<SourceSyncResult> SyncSourceAsync(SourceConfiguration source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        Directory.CreateDirectory(_layout.RootDirectory);
        Directory.CreateDirectory(_layout.ManifestsDirectory);
        Directory.CreateDirectory(_layout.SourcesDirectory);
        Directory.CreateDirectory(_layout.StagingDirectory);

        var previousManifest = LoadManifest(source.Id);
        var previousAssetsByName = previousManifest?.CurrentAssets.ToDictionary(asset => asset.AssetName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CurrentAssetRecord>(StringComparer.OrdinalIgnoreCase);

        var remoteAssets = (await _client.GetReleaseAssetsAsync(source, cancellationToken))
            .Where(asset => asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentAssets = new List<CurrentAssetRecord>();
        var downloaded = new List<string>();
        var stagingDirectory = _layout.GetSourceStagingDirectory(source.Id);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            foreach (var remoteAsset in remoteAssets)
            {
                if (previousAssetsByName.TryGetValue(remoteAsset.Name, out var currentRecord) &&
                    currentRecord.Id == remoteAsset.Id &&
                    currentRecord.Size == remoteAsset.Size &&
                    currentRecord.UpdatedAtUtc == remoteAsset.UpdatedAt &&
                    File.Exists(currentRecord.FullPath))
                {
                    currentAssets.Add(currentRecord);
                    continue;
                }

                var downloadedPath = await _client.DownloadAssetAsync(source, remoteAsset, stagingDirectory, cancellationToken);
                var sha256 = await ComputeSha256Async(downloadedPath, cancellationToken);
                var finalPath = _layout.GetAssetPath(source.Id, remoteAsset.Name, sha256);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                if (!File.Exists(finalPath))
                {
                    File.Move(downloadedPath, finalPath);
                }
                else
                {
                    File.Delete(downloadedPath);
                }

                currentAssets.Add(new CurrentAssetRecord
                {
                    Id = remoteAsset.Id,
                    AssetName = remoteAsset.Name,
                    Size = remoteAsset.Size,
                    UpdatedAtUtc = remoteAsset.UpdatedAt,
                    Sha256 = sha256,
                    FullPath = finalPath
                });
                downloaded.Add(remoteAsset.Name);
            }

            var manifest = new SourceManifest
            {
                SourceId = source.Id,
                Repo = source.Repo,
                Tag = source.Tag,
                SyncedAtUtc = DateTimeOffset.UtcNow,
                CurrentAssets = currentAssets
            };

            await SaveManifestAsync(manifest, cancellationToken);
            return new SourceSyncResult
            {
                Manifest = manifest,
                DownloadedAssets = downloaded
            };
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public string ResolveExecutorPath(ModServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var manifest = LoadManifest(configuration.Executor.Source)
            ?? throw new FileNotFoundException($"No manifest found for executor source '{configuration.Executor.Source}'.");

        var asset = manifest.CurrentAssets.FirstOrDefault(asset =>
            string.Equals(asset.AssetName, configuration.Executor.Asset, StringComparison.OrdinalIgnoreCase));

        return asset?.FullPath
            ?? throw new FileNotFoundException($"Executor asset '{configuration.Executor.Asset}' was not found in source '{configuration.Executor.Source}'.");
    }

    public IReadOnlyList<SourceAsset> LoadCurrentAssets(IEnumerable<string>? sourceIds = null)
    {
        Directory.CreateDirectory(_layout.ManifestsDirectory);
        var filter = sourceIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var assets = new List<SourceAsset>();
        foreach (var manifestPath in Directory.EnumerateFiles(_layout.ManifestsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var manifest = JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null)
            {
                continue;
            }

            if (filter is not null && !filter.Contains(manifest.SourceId))
            {
                continue;
            }

            assets.AddRange(manifest.CurrentAssets.Select(asset => new SourceAsset
            {
                SourceId = manifest.SourceId,
                AssetName = asset.AssetName,
                FullPath = asset.FullPath
            }));
        }

        return assets;
    }

    public CleanupResult CleanupStaleFiles()
    {
        var status = GetCleanupStatus();
        if (status.StaleFileCount == 0 || status.LockedFiles.Count > 0)
        {
            return status;
        }

        foreach (var staleFile in status.StaleFiles)
        {
            File.Delete(staleFile);
            PruneEmptyDirectories(Path.GetDirectoryName(staleFile));
        }

        return new CleanupResult
        {
            StaleFileCount = status.StaleFileCount,
            Deleted = true,
            LockedFiles = [],
            StaleFiles = status.StaleFiles
        };
    }

    public CleanupResult GetCleanupStatus()
    {
        Directory.CreateDirectory(_layout.SourcesDirectory);
        Directory.CreateDirectory(_layout.ManifestsDirectory);

        var currentFiles = LoadCurrentAssets()
            .Select(asset => Path.GetFullPath(asset.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var staleFiles = Directory.EnumerateFiles(_layout.SourcesDirectory, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !currentFiles.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (staleFiles.Count == 0)
        {
            return new CleanupResult
            {
                StaleFileCount = 0,
                Deleted = false,
                LockedFiles = [],
                StaleFiles = []
            };
        }

        var lockedFiles = staleFiles.Where(FileLockProbe.IsLocked).ToList();
        return new CleanupResult
        {
            StaleFileCount = staleFiles.Count,
            Deleted = false,
            LockedFiles = lockedFiles,
            StaleFiles = staleFiles
        };
    }

    public SourceManifest? LoadManifest(string sourceId)
    {
        var path = _layout.GetManifestPath(sourceId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path), JsonOptions);
    }

    private async Task SaveManifestAsync(SourceManifest manifest, CancellationToken cancellationToken)
    {
        var path = _layout.GetManifestPath(manifest.SourceId);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void PruneEmptyDirectories(string? startDirectory)
    {
        var directory = startDirectory;
        while (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                break;
            }

            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
