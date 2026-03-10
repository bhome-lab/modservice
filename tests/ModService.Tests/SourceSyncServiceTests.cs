using System.Text;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.Tests;

public sealed class SourceSyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SyncAsync_DownloadsAssets_AndResolvesExecutor()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddAsset("mods", "injector.dll", "executor-v1");
        client.AddAsset("mods", "sample.dll", "module-v1");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "injector.dll" },
            Sources = [new SourceConfiguration { Id = "mods", Repo = "owner/repo", Tag = "stable" }]
        };

        var results = await service.SyncAsync(configuration, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, results[0].Manifest.CurrentAssets.Count);
        Assert.NotEmpty(results[0].DownloadedAssets);
        Assert.True(File.Exists(service.ResolveExecutorPath(configuration)));

        var currentAssets = service.LoadCurrentAssets(["mods"]);
        Assert.Equal(2, currentAssets.Count);
    }

    [Fact]
    public async Task SyncAsync_ReusesCurrentAsset_WhenMetadataMatches()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddAsset("mods", "injector.dll", "executor-v1");

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
    }

    [Fact]
    public async Task CleanupStaleFiles_SkipsAll_WhenAnyStaleFileIsLocked()
    {
        Directory.CreateDirectory(_root);
        var client = new FakeGitHubReleaseClient();
        client.AddAsset("mods", "injector.dll", "executor-v1");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "mods", Asset = "injector.dll" },
            Sources = [new SourceConfiguration { Id = "mods", Repo = "owner/repo", Tag = "stable" }]
        };

        await service.SyncAsync(configuration, CancellationToken.None);
        client.ReplaceAsset("mods", "injector.dll", "executor-v2");
        await service.SyncAsync(configuration, CancellationToken.None);

        var allFiles = Directory.EnumerateFiles(Path.Combine(_root, "sources"), "*.dll", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
        Assert.Equal(2, allFiles.Length);
        var currentFile = service.ResolveExecutorPath(configuration);
        var staleFile = allFiles.Single(path => !string.Equals(path, currentFile, StringComparison.OrdinalIgnoreCase));

        using var stream = new FileStream(staleFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(FileLockProbe.IsLocked(staleFile));
        var lockedCleanup = service.CleanupStaleFiles();
        Assert.False(lockedCleanup.Deleted);
        Assert.NotEmpty(lockedCleanup.LockedFiles);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_root, "sources"), "*.dll", SearchOption.AllDirectories).Count());

        stream.Dispose();
        var cleanup = service.CleanupStaleFiles();
        Assert.True(cleanup.Deleted);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "sources"), "*.dll", SearchOption.AllDirectories));
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

        public void AddAsset(string sourceId, string assetName, string content)
        {
            if (!_assets.TryGetValue(sourceId, out var sourceAssets))
            {
                sourceAssets = new Dictionary<string, FakeAsset>(StringComparer.OrdinalIgnoreCase);
                _assets[sourceId] = sourceAssets;
            }

            sourceAssets[assetName] = new FakeAsset(_nextId++, assetName, content, DateTimeOffset.UtcNow);
        }

        public void ReplaceAsset(string sourceId, string assetName, string content)
            => AddAsset(sourceId, assetName, content);

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
                    Size = Encoding.UTF8.GetByteCount(asset.Content),
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
            File.WriteAllText(path, data.Content, Encoding.UTF8);
            return Task.FromResult(path);
        }

        private sealed record FakeAsset(long Id, string Name, string Content, DateTimeOffset UpdatedAt);
    }
}
