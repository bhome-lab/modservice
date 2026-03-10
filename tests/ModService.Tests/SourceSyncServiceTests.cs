using System.IO.Compression;
using System.Text;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Tests;

public sealed class SourceSyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SyncAsync_DownloadsSelectedDirectAssets_AndKeepsSiblingFiles()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddTextAsset("mods", "NativeExecutor.dll", "executor-v1");
        client.AddTextAsset("mods", "SampleModule.dll", "module-v1");
        client.AddTextAsset("mods", "readme.txt", "sidecar");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "NativeExecutor.dll" },
            Sources = [new SourceConfiguration { Id = "mods", Repo = "owner/repo", Tag = "stable" }]
        };

        var results = await service.SyncAsync(configuration, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(3, results[0].DownloadedAssets.Count);
        Assert.Equal(2, results[0].Manifest.CurrentAssets.Count);
        Assert.NotNull(results[0].Manifest.CurrentDirectSnapshot);
        Assert.True(File.Exists(service.ResolveExecutorPath(configuration)));
        Assert.True(File.Exists(Path.Combine(results[0].Manifest.CurrentDirectSnapshot!.RootPath, "readme.txt")));
        Assert.DoesNotContain(results[0].Manifest.CurrentAssets, item => string.Equals(item.AssetName, "readme.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncAsync_ReusesCurrentDirectSnapshot_WhenMetadataMatches()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddTextAsset("mods", "injector.dll", "executor-v1");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "injector.dll" },
            Sources = [new SourceConfiguration { Id = "mods", Repo = "owner/repo", Tag = "stable" }]
        };

        var first = await service.SyncAsync(configuration, CancellationToken.None);
        var second = await service.SyncAsync(configuration, CancellationToken.None);

        Assert.Single(first[0].DownloadedAssets);
        Assert.Empty(second[0].DownloadedAssets);
        Assert.Equal(first[0].Manifest.CurrentDirectSnapshot!.RootPath, second[0].Manifest.CurrentDirectSnapshot!.RootPath);
    }

    [Fact]
    public async Task SyncAsync_ExtractsArchive_ExposesDlls_AndKeepsAllFiles()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddZipAsset(
            "mods",
            "native-bundle-release.zip",
            new Dictionary<string, byte[]>
            {
                ["native/skruntime.dll"] = Encoding.UTF8.GetBytes("runtime"),
                ["native/skruntime.lib"] = Encoding.UTF8.GetBytes("lib"),
                ["native_core/skruntime.dll"] = Encoding.UTF8.GetBytes("runtime-core"),
                ["docs/readme.txt"] = Encoding.UTF8.GetBytes("docs")
            });

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "native/skruntime.dll" },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Archives =
                    [
                        new ArchiveConfiguration { Asset = "native-bundle-release.zip" }
                    ]
                }
            ]
        };

        var results = await service.SyncAsync(configuration, CancellationToken.None);

        Assert.Single(results);
        Assert.Single(results[0].DownloadedAssets);
        Assert.Equal(["native/skruntime.dll", "native_core/skruntime.dll"], results[0].Manifest.CurrentAssets.Select(item => item.AssetName).ToArray());
        Assert.True(File.Exists(service.ResolveExecutorPath(configuration)));

        var archiveRoot = Assert.Single(results[0].Manifest.ProcessedArchives).RootPath;
        Assert.True(File.Exists(Path.Combine(archiveRoot, "native", "skruntime.lib")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "docs", "readme.txt")));
    }

    [Fact]
    public async Task SyncAsync_ReusesArchiveRoot_WhenOnlySelectionChanges()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddZipAsset(
            "mods",
            "bundle.zip",
            new Dictionary<string, byte[]>
            {
                ["native/one.dll"] = Encoding.UTF8.GetBytes("one"),
                ["native/two.dll"] = Encoding.UTF8.GetBytes("two"),
                ["native/one.ini"] = Encoding.UTF8.GetBytes("cfg")
            });

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var broadConfiguration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "native/one.dll" },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Archives =
                    [
                        new ArchiveConfiguration { Asset = "bundle.zip" }
                    ]
                }
            ]
        };

        var first = await service.SyncAsync(broadConfiguration, CancellationToken.None);

        var narrowedConfiguration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "native/one.dll" },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Archives =
                    [
                        new ArchiveConfiguration
                        {
                            Asset = "bundle.zip",
                            Include = ["native/one.dll"]
                        }
                    ]
                }
            ]
        };

        var second = await service.SyncAsync(narrowedConfiguration, CancellationToken.None);

        Assert.Single(first[0].DownloadedAssets);
        Assert.Empty(second[0].DownloadedAssets);
        Assert.Equal(first[0].Manifest.ProcessedArchives[0].RootPath, second[0].Manifest.ProcessedArchives[0].RootPath);
        Assert.Equal(["native/one.dll"], second[0].Manifest.CurrentAssets.Select(item => item.AssetName).ToArray());
        Assert.True(File.Exists(Path.Combine(second[0].Manifest.ProcessedArchives[0].RootPath, "native", "one.ini")));
    }

    [Fact]
    public async Task SyncAsync_RejectsDuplicateLogicalAssetNames()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddTextAsset("mods", "one.dll", "direct");
        client.AddZipAsset(
            "mods",
            "bundle.zip",
            new Dictionary<string, byte[]>
            {
                ["one.dll"] = Encoding.UTF8.GetBytes("archive")
            });

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "one.dll" },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Archives =
                    [
                        new ArchiveConfiguration { Asset = "bundle.zip" }
                    ]
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(configuration, CancellationToken.None));
        Assert.Contains("duplicate logical asset names", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one.dll", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncAsync_RejectsRootedArchiveEntries()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddZipAsset(
            "mods",
            "bundle.zip",
            new Dictionary<string, byte[]>
            {
                ["C:/cache/rooted/evil.dll"] = Encoding.UTF8.GetBytes("evil")
            });

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "evil.dll" },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Archives =
                    [
                        new ArchiveConfiguration { Asset = "bundle.zip" }
                    ]
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync(configuration, CancellationToken.None));
        Assert.Contains("rooted path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupStaleFiles_SkipsAll_WhenAnyStaleFileIsLocked()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddTextAsset("mods", "injector.dll", "executor-v1");
        client.AddTextAsset("mods", "settings.json", "config-v1");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "injector.dll" },
            Sources = [new SourceConfiguration { Id = "mods", Repo = "owner/repo", Tag = "stable" }]
        };

        await service.SyncAsync(configuration, CancellationToken.None);
        client.ReplaceTextAsset("mods", "injector.dll", "executor-v2");
        client.ReplaceTextAsset("mods", "settings.json", "config-v2");
        await service.SyncAsync(configuration, CancellationToken.None);

        var directRoots = Directory.EnumerateDirectories(Path.Combine(_root, "sources", "mods", "direct"), "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(2, directRoots.Length);

        var currentRoot = service.LoadManifest("mods")!.CurrentDirectSnapshot!.RootPath;
        var staleRoot = directRoots.Single(path => !string.Equals(path, currentRoot, StringComparison.OrdinalIgnoreCase));
        var staleSidecar = Path.Combine(staleRoot, "settings.json");

        using var stream = new FileStream(staleSidecar, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(FileLockProbe.IsLocked(staleSidecar));
        var lockedCleanup = service.CleanupStaleFiles();
        Assert.False(lockedCleanup.Deleted);
        Assert.NotEmpty(lockedCleanup.LockedFiles);
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(_root, "sources"), "*", SearchOption.AllDirectories).Count());

        stream.Dispose();
        var cleanup = service.CleanupStaleFiles();
        Assert.True(cleanup.Deleted);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_root, "sources"), "*", SearchOption.AllDirectories).Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeGitHubReleaseClient : IGitHubReleaseClient
    {
        private readonly Dictionary<string, Dictionary<string, FakeAsset>> _assets = new(StringComparer.OrdinalIgnoreCase);
        private long _nextId = 1;

        public void AddTextAsset(string sourceId, string assetName, string content)
            => AddBinaryAsset(sourceId, assetName, Encoding.UTF8.GetBytes(content));

        public void ReplaceTextAsset(string sourceId, string assetName, string content)
            => AddTextAsset(sourceId, assetName, content);

        public void AddZipAsset(string sourceId, string assetName, IReadOnlyDictionary<string, byte[]> entries)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in entries)
                {
                    var zipEntry = archive.CreateEntry(entry.Key);
                    using var stream = zipEntry.Open();
                    stream.Write(entry.Value, 0, entry.Value.Length);
                }
            }

            AddBinaryAsset(sourceId, assetName, memory.ToArray());
        }

        public Task<IReadOnlyList<GitHubReleaseAsset>> GetReleaseAssetsAsync(SourceConfiguration source, CancellationToken cancellationToken)
        {
            if (!_assets.TryGetValue(source.Id, out var sourceAssets))
            {
                return Task.FromResult<IReadOnlyList<GitHubReleaseAsset>>([]);
            }

            var assets = sourceAssets.Values
                .Select(asset => new GitHubReleaseAsset
                {
                    Id = asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = asset.Name,
                    Size = asset.Content.Length,
                    UpdatedAt = asset.UpdatedAt
                })
                .ToArray();

            return Task.FromResult<IReadOnlyList<GitHubReleaseAsset>>(assets);
        }

        public Task<string> DownloadAssetAsync(SourceConfiguration source, GitHubReleaseAsset asset, string destinationDirectory, CancellationToken cancellationToken)
        {
            var data = _assets[source.Id][asset.Name];
            Directory.CreateDirectory(destinationDirectory);
            var path = Path.Combine(destinationDirectory, asset.Name);
            File.WriteAllBytes(path, data.Content);
            return Task.FromResult(path);
        }

        private void AddBinaryAsset(string sourceId, string assetName, byte[] content)
        {
            if (!_assets.TryGetValue(sourceId, out var sourceAssets))
            {
                sourceAssets = new Dictionary<string, FakeAsset>(StringComparer.OrdinalIgnoreCase);
                _assets[sourceId] = sourceAssets;
            }

            sourceAssets[assetName] = new FakeAsset(_nextId++, assetName, content, DateTimeOffset.UtcNow);
        }

        private sealed record FakeAsset(long Id, string Name, byte[] Content, DateTimeOffset UpdatedAt);
    }
}
