using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Execution;
using ModService.Core.Matching;

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
        Directory.CreateDirectory(_layout.GetSourceDirectory(source.Id));
        Directory.CreateDirectory(_layout.GetDirectDirectory(source.Id));
        Directory.CreateDirectory(_layout.GetArchiveDirectory(source.Id));

        var previousManifest = LoadManifest(source.Id);
        var remoteAssets = (await _client.GetReleaseAssetsAsync(source, cancellationToken))
            .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remoteAssetsByName = remoteAssets.ToDictionary(asset => asset.Name, StringComparer.OrdinalIgnoreCase);

        var stagingDirectory = _layout.GetSourceStagingDirectory(source.Id);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var archiveAssetNames = source.Archives
                .Select(archive => archive.Asset)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var directAssets = remoteAssets
                .Where(asset => !archiveAssetNames.Contains(asset.Name))
                .Where(asset => MatchesAssetSelection(source.Include, source.Exclude, asset.Name))
                .ToList();

            var directSnapshot = await BuildDirectSnapshotAsync(source, previousManifest, directAssets, stagingDirectory, cancellationToken);
            var archiveResult = await ProcessArchivesAsync(source, previousManifest, remoteAssetsByName, stagingDirectory, cancellationToken);

            var currentAssets = new List<CurrentAssetRecord>();
            if (directSnapshot.Snapshot is not null)
            {
                currentAssets.AddRange(BuildCurrentAssetsFromDirectSnapshot(directSnapshot.Snapshot));
            }

            foreach (var processedArchive in archiveResult.Records)
            {
                currentAssets.AddRange(processedArchive.SelectedAssets);
            }

            currentAssets = currentAssets
                .OrderBy(asset => asset.AssetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ValidateUniqueLogicalAssetNames(source.Id, currentAssets);

            var manifest = new SourceManifest
            {
                SourceId = source.Id,
                Repo = source.Repo,
                Tag = source.Tag,
                SyncedAtUtc = DateTimeOffset.UtcNow,
                CurrentDirectSnapshot = directSnapshot.Snapshot,
                ProcessedArchives = archiveResult.Records,
                CurrentAssets = currentAssets
            };

            await SaveManifestAsync(manifest, cancellationToken);
            return new SourceSyncResult
            {
                Manifest = manifest,
                DownloadedAssets = directSnapshot.DownloadedAssets
                    .Concat(archiveResult.DownloadedAssets)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
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

        var asset = manifest.CurrentAssets.FirstOrDefault(item =>
            string.Equals(item.AssetName, configuration.Executor.Asset, StringComparison.OrdinalIgnoreCase));

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

        foreach (var staleRoot in EnumerateStaleRoots())
        {
            if (Directory.Exists(staleRoot))
            {
                Directory.Delete(staleRoot, recursive: true);
                PruneEmptyDirectories(Path.GetDirectoryName(staleRoot), _layout.SourcesDirectory);
            }
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

        var staleRoots = EnumerateStaleRoots().ToList();
        if (staleRoots.Count == 0)
        {
            return new CleanupResult
            {
                StaleFileCount = 0,
                Deleted = false,
                LockedFiles = [],
                StaleFiles = []
            };
        }

        var staleFiles = staleRoots
            .SelectMany(root => Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                : [])
            .Select(Path.GetFullPath)
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

    private async Task<DirectSnapshotBuildResult> BuildDirectSnapshotAsync(
        SourceConfiguration source,
        SourceManifest? previousManifest,
        IReadOnlyList<GitHubReleaseAsset> directAssets,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        if (directAssets.Count == 0)
        {
            return new DirectSnapshotBuildResult(null, []);
        }

        var previousSnapshot = previousManifest?.CurrentDirectSnapshot;
        if (CanReuseDirectSnapshot(previousSnapshot, directAssets))
        {
            return new DirectSnapshotBuildResult(previousSnapshot, []);
        }

        var previousFilesByName = previousSnapshot?.Files.ToDictionary(file => file.AssetName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DirectFileRecord>(StringComparer.OrdinalIgnoreCase);

        var downloadsDirectory = Path.Combine(stagingDirectory, "downloads-direct");
        var tempRoot = Path.Combine(stagingDirectory, "direct-root");
        Directory.CreateDirectory(downloadsDirectory);
        Directory.CreateDirectory(tempRoot);

        var tempFiles = new List<DirectFileRecord>();
        var downloaded = new List<string>();

        foreach (var asset in directAssets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            ValidateDirectAssetName(asset.Name);

            var destinationPath = Path.Combine(tempRoot, asset.Name);
            if (previousFilesByName.TryGetValue(asset.Name, out var previousFile) &&
                MetadataMatches(previousFile, asset) &&
                File.Exists(previousFile.FullPath))
            {
                File.Copy(previousFile.FullPath, destinationPath, overwrite: true);
                tempFiles.Add(new DirectFileRecord
                {
                    Id = asset.Id,
                    AssetName = asset.Name,
                    Size = asset.Size,
                    UpdatedAtUtc = asset.UpdatedAt,
                    Sha256 = previousFile.Sha256,
                    FullPath = destinationPath
                });
                continue;
            }

            var downloadedPath = await _client.DownloadAssetAsync(source, asset, downloadsDirectory, cancellationToken);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(downloadedPath, destinationPath);
            var sha256 = await ComputeSha256Async(destinationPath, cancellationToken);
            tempFiles.Add(new DirectFileRecord
            {
                Id = asset.Id,
                AssetName = asset.Name,
                Size = asset.Size,
                UpdatedAtUtc = asset.UpdatedAt,
                Sha256 = sha256,
                FullPath = destinationPath
            });
            downloaded.Add(asset.Name);
        }

        var revisionHash = ComputeDirectSnapshotHash(tempFiles);
        var finalRoot = _layout.GetDirectRevisionRoot(source.Id, revisionHash);
        if (!Directory.Exists(finalRoot))
        {
            Directory.Move(tempRoot, finalRoot);
        }
        else
        {
            Directory.Delete(tempRoot, recursive: true);
        }

        var finalFiles = tempFiles
            .Select(file => new DirectFileRecord
            {
                Id = file.Id,
                AssetName = file.AssetName,
                Size = file.Size,
                UpdatedAtUtc = file.UpdatedAtUtc,
                Sha256 = file.Sha256,
                FullPath = Path.Combine(finalRoot, file.AssetName)
            })
            .ToList();

        return new DirectSnapshotBuildResult(
            new DirectSnapshotRecord
            {
                RevisionHash = revisionHash,
                RootPath = finalRoot,
                Files = finalFiles
            },
            downloaded);
    }

    private async Task<ArchiveProcessResult> ProcessArchivesAsync(
        SourceConfiguration source,
        SourceManifest? previousManifest,
        IReadOnlyDictionary<string, GitHubReleaseAsset> remoteAssetsByName,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var records = new List<ProcessedArchiveRecord>();
        var downloaded = new List<string>();
        var previousArchivesByName = previousManifest?.ProcessedArchives.ToDictionary(record => record.AssetName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ProcessedArchiveRecord>(StringComparer.OrdinalIgnoreCase);
        var downloadsDirectory = Path.Combine(stagingDirectory, "downloads-archives");
        Directory.CreateDirectory(downloadsDirectory);

        foreach (var archive in source.Archives)
        {
            if (!remoteAssetsByName.TryGetValue(archive.Asset, out var remoteAsset))
            {
                continue;
            }

            var selectionKey = ComputeSelectionKey(archive);
            if (previousArchivesByName.TryGetValue(archive.Asset, out var previousRecord) &&
                CanReuseArchive(previousRecord, remoteAsset, selectionKey))
            {
                records.Add(previousRecord);
                continue;
            }

            string archiveSha;
            string rootPath;
            if (previousRecord is not null &&
                MetadataMatches(previousRecord, remoteAsset) &&
                !string.IsNullOrWhiteSpace(previousRecord.RootPath) &&
                Directory.Exists(previousRecord.RootPath))
            {
                archiveSha = previousRecord.ArchiveSha256;
                rootPath = previousRecord.RootPath;
            }
            else
            {
                var downloadedPath = await _client.DownloadAssetAsync(source, remoteAsset, downloadsDirectory, cancellationToken);
                archiveSha = await ComputeSha256Async(downloadedPath, cancellationToken);
                rootPath = _layout.GetArchiveRevisionRoot(source.Id, remoteAsset.Name, archiveSha);

                if (!Directory.Exists(rootPath))
                {
                    var tempExtractRoot = Path.Combine(stagingDirectory, "archive-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempExtractRoot);
                    ExtractArchive(downloadedPath, tempExtractRoot);
                    Directory.CreateDirectory(_layout.GetArchiveAssetDirectory(source.Id, remoteAsset.Name));

                    if (!Directory.Exists(rootPath))
                    {
                        Directory.Move(tempExtractRoot, rootPath);
                    }
                    else
                    {
                        Directory.Delete(tempExtractRoot, recursive: true);
                    }
                }

                downloaded.Add(remoteAsset.Name);
            }

            var selectedAssets = await BuildSelectedArchiveAssetsAsync(remoteAsset, rootPath, archive, cancellationToken);
            records.Add(new ProcessedArchiveRecord
            {
                Id = remoteAsset.Id,
                AssetName = remoteAsset.Name,
                Size = remoteAsset.Size,
                UpdatedAtUtc = remoteAsset.UpdatedAt,
                ArchiveSha256 = archiveSha,
                RootPath = rootPath,
                SelectionKey = selectionKey,
                SelectedAssets = selectedAssets
            });
        }

        return new ArchiveProcessResult(records, downloaded);
    }

    private static List<CurrentAssetRecord> BuildCurrentAssetsFromDirectSnapshot(DirectSnapshotRecord snapshot)
        => snapshot.Files
            .Where(file => file.AssetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(file => new CurrentAssetRecord
            {
                Id = file.Id,
                AssetName = file.AssetName,
                Size = file.Size,
                UpdatedAtUtc = file.UpdatedAtUtc,
                Sha256 = file.Sha256,
                FullPath = file.FullPath
            })
            .ToList();

    private async Task<List<CurrentAssetRecord>> BuildSelectedArchiveAssetsAsync(
        GitHubReleaseAsset archiveAsset,
        string rootPath,
        ArchiveConfiguration archiveConfiguration,
        CancellationToken cancellationToken)
    {
        var assets = new List<CurrentAssetRecord>();
        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, filePath);
            var logicalAssetName = NormalizeRelativePath(relativePath);
            if (!MatchesAssetSelection(archiveConfiguration.Include, archiveConfiguration.Exclude, logicalAssetName))
            {
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            assets.Add(new CurrentAssetRecord
            {
                Id = archiveAsset.Id,
                AssetName = logicalAssetName,
                Size = fileInfo.Length,
                UpdatedAtUtc = archiveAsset.UpdatedAt,
                Sha256 = await ComputeSha256Async(filePath, cancellationToken),
                FullPath = Path.GetFullPath(filePath)
            });
        }

        return assets;
    }

    private static void ValidateUniqueLogicalAssetNames(string sourceId, IEnumerable<CurrentAssetRecord> currentAssets)
    {
        var duplicates = currentAssets
            .GroupBy(asset => asset.AssetName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (duplicates.Count == 0)
        {
            return;
        }

        var details = string.Join(
            "; ",
            duplicates.Select(group => $"{group.Key}: {string.Join(", ", group.Select(asset => asset.FullPath))}"));
        throw new InvalidOperationException(
            $"Source '{sourceId}' produced duplicate logical asset names. Each exposed DLL name must be unique within a source. {details}");
    }

    private IEnumerable<string> EnumerateStaleRoots()
    {
        var currentRoots = Directory.EnumerateFiles(_layout.ManifestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path), JsonOptions))
            .Where(manifest => manifest is not null)
            .SelectMany(manifest => EnumerateCurrentRoots(manifest!))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return EnumerateRevisionRoots()
            .Where(root => !currentRoots.Contains(Path.GetFullPath(root)))
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<string> EnumerateRevisionRoots()
    {
        if (!Directory.Exists(_layout.SourcesDirectory))
        {
            yield break;
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(_layout.SourcesDirectory))
        {
            var directDirectory = Path.Combine(sourceDirectory, "direct");
            if (Directory.Exists(directDirectory))
            {
                foreach (var root in Directory.EnumerateDirectories(directDirectory))
                {
                    yield return root;
                }
            }

            var archivesDirectory = Path.Combine(sourceDirectory, "archives");
            if (Directory.Exists(archivesDirectory))
            {
                foreach (var archiveDirectory in Directory.EnumerateDirectories(archivesDirectory))
                {
                    foreach (var root in Directory.EnumerateDirectories(archiveDirectory))
                    {
                        yield return root;
                    }
                }
            }

            foreach (var legacyAssetDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                var name = Path.GetFileName(legacyAssetDirectory);
                if (string.Equals(name, "direct", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "archives", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var root in Directory.EnumerateDirectories(legacyAssetDirectory))
                {
                    yield return root;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCurrentRoots(SourceManifest manifest)
    {
        if (manifest.CurrentDirectSnapshot is not null && Directory.Exists(manifest.CurrentDirectSnapshot.RootPath))
        {
            yield return manifest.CurrentDirectSnapshot.RootPath;
        }

        foreach (var archive in manifest.ProcessedArchives)
        {
            if (Directory.Exists(archive.RootPath))
            {
                yield return archive.RootPath;
            }
        }
    }

    private static bool MatchesAssetSelection(IEnumerable<string> include, IEnumerable<string> exclude, string value)
        => MatchesAny(include, NormalizeRelativePath(value), defaultIfEmpty: true) &&
           !MatchesAny(exclude, NormalizeRelativePath(value), defaultIfEmpty: false);

    private static bool MatchesAny(IEnumerable<string> patterns, string value, bool defaultIfEmpty)
    {
        var any = false;
        foreach (var pattern in patterns)
        {
            any = true;
            if (GlobPattern.IsMatch(NormalizeRelativePath(pattern), value))
            {
                return true;
            }
        }

        return !any && defaultIfEmpty;
    }

    private static string ComputeSelectionKey(ArchiveConfiguration archive)
    {
        var include = archive.Include.Select(NormalizeRelativePath).OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        var exclude = archive.Exclude.Select(NormalizeRelativePath).OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        var payload = string.Join('\n', include) + "\n--\n" + string.Join('\n', exclude);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeDirectSnapshotHash(IEnumerable<DirectFileRecord> files)
    {
        var payload = string.Join(
            '\n',
            files.OrderBy(file => file.AssetName, StringComparer.OrdinalIgnoreCase)
                .Select(file => $"{file.AssetName}|{file.Sha256}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool CanReuseDirectSnapshot(DirectSnapshotRecord? snapshot, IReadOnlyList<GitHubReleaseAsset> directAssets)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.RootPath) || !Directory.Exists(snapshot.RootPath))
        {
            return false;
        }

        if (snapshot.Files.Count != directAssets.Count)
        {
            return false;
        }

        var filesByName = snapshot.Files.ToDictionary(file => file.AssetName, StringComparer.OrdinalIgnoreCase);
        foreach (var asset in directAssets)
        {
            if (!filesByName.TryGetValue(asset.Name, out var existingFile))
            {
                return false;
            }

            if (!MetadataMatches(existingFile, asset) || !File.Exists(existingFile.FullPath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanReuseArchive(ProcessedArchiveRecord record, GitHubReleaseAsset asset, string selectionKey)
    {
        if (!MetadataMatches(record, asset) ||
            !string.Equals(record.SelectionKey, selectionKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(record.RootPath) ||
            !Directory.Exists(record.RootPath))
        {
            return false;
        }

        return record.SelectedAssets.All(selectedAsset => File.Exists(selectedAsset.FullPath));
    }

    private static bool MetadataMatches(DirectFileRecord file, GitHubReleaseAsset asset)
        => string.Equals(file.Id, asset.Id, StringComparison.Ordinal) &&
           file.Size == asset.Size &&
           file.UpdatedAtUtc == asset.UpdatedAt;

    private static bool MetadataMatches(ProcessedArchiveRecord record, GitHubReleaseAsset asset)
        => string.Equals(record.Id, asset.Id, StringComparison.Ordinal) &&
           record.Size == asset.Size &&
           record.UpdatedAtUtc == asset.UpdatedAt;

    private static void ExtractArchive(string archivePath, string destinationRoot)
    {
        var fullDestinationRoot = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var normalizedPath = NormalizeArchiveEntryPath(entry.FullName);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            var fullDestinationPath = Path.GetFullPath(destinationPath);
            if (!fullDestinationPath.StartsWith(fullDestinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' resolves outside the destination root.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(fullDestinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
            entry.ExtractToFile(fullDestinationPath, overwrite: true);
        }
    }

    private static string NormalizeArchiveEntryPath(string path)
    {
        var normalized = NormalizeRelativePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(normalized.Replace('/', Path.DirectorySeparatorChar)))
        {
            throw new InvalidDataException($"Archive entry '{path}' contains an unsupported rooted path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Archive entry '{path}' contains unsupported path traversal.");
        }

        return string.Join('/', segments);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static void ValidateDirectAssetName(string assetName)
    {
        if (assetName.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new InvalidDataException($"Direct release asset '{assetName}' must not contain path separators.");
        }
    }

    private static void PruneEmptyDirectories(string? startDirectory, string stopDirectory)
    {
        var current = startDirectory;
        var stop = Path.GetFullPath(stopDirectory);
        while (!string.IsNullOrWhiteSpace(current) &&
               Directory.Exists(current) &&
               !string.Equals(Path.GetFullPath(current), stop, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record DirectSnapshotBuildResult(DirectSnapshotRecord? Snapshot, IReadOnlyList<string> DownloadedAssets);

    private sealed record ArchiveProcessResult(List<ProcessedArchiveRecord> Records, IReadOnlyList<string> DownloadedAssets);
}
