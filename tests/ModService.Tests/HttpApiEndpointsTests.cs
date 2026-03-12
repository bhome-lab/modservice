using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModService.Core.Configuration;
using ModService.Core.Updates;
using ModService.Host;

namespace ModService.Tests;

public sealed class HttpApiEndpointsTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceApiTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DllsEndpoint_ReturnsAbsolutePaths_FilteredByGlob()
    {
        Directory.CreateDirectory(_root);
        var configuration = CreateConfiguration();
        var syncService = await CreateSyncedServiceAsync(configuration);
        using var configurationStore = CreateConfigurationStore(configuration);
        var runtimeState = new RuntimeStateStore();

        await using var host = await StartHostAsync(configurationStore, runtimeState, syncService);
        using var response = await host.Client.GetAsync("/api/v1/dlls?glob=Sample*.dll");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var item = items[0];
        Assert.Equal("repo", item.GetProperty("sourceId").GetString());
        Assert.Equal("SampleModule.dll", item.GetProperty("assetName").GetString());
        var fullPath = item.GetProperty("fullPath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fullPath));
        Assert.True(Path.IsPathRooted(fullPath));
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task SourceDllsEndpoint_ReturnsNotFound_ForUnknownSource()
    {
        Directory.CreateDirectory(_root);
        var configuration = CreateConfiguration();
        var syncService = await CreateSyncedServiceAsync(configuration);
        using var configurationStore = CreateConfigurationStore(configuration);
        var runtimeState = new RuntimeStateStore();

        await using var host = await StartHostAsync(configurationStore, runtimeState, syncService);
        using var response = await host.Client.GetAsync("/api/v1/sources/missing/dlls");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("source_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StatusEndpoint_ReportsGitHubRateLimitState()
    {
        Directory.CreateDirectory(_root);
        var configuration = CreateConfiguration();
        var syncService = await CreateSyncedServiceAsync(configuration);
        using var configurationStore = CreateConfigurationStore(configuration);
        var runtimeState = new RuntimeStateStore();
        runtimeState.SetGitHubRateLimited(
            limit: 60,
            remaining: 0,
            resetAtUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            backoffUntilUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            scope: "core",
            message: "GitHub API rate limit exceeded.");

        await using var host = await StartHostAsync(configurationStore, runtimeState, syncService);
        using var response = await host.Client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var github = document.RootElement.GetProperty("github");
        Assert.Equal("rate_limited", github.GetProperty("state").GetString());
        Assert.Equal("core", github.GetProperty("rateLimit").GetProperty("scope").GetString());
        Assert.True(github.GetProperty("rateLimit").GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<SourceSyncService> CreateSyncedServiceAsync(ModServiceConfiguration configuration)
    {
        var client = new FakeGitHubReleaseClient();
        client.AddTextAsset("repo", "NativeExecutor.dll", "executor-v1");
        client.AddTextAsset("repo", "SampleModule.dll", "module-v1");
        client.AddTextAsset("repo", "Ignored.txt", "ignored");

        var service = new SourceSyncService(new StorageLayout(_root), client);
        await service.SyncAsync(configuration, CancellationToken.None);
        return service;
    }

    private static EffectiveConfigurationStore CreateConfigurationStore(ModServiceConfiguration configuration)
        => new(new TestOptionsMonitor(configuration), NullLogger<EffectiveConfigurationStore>.Instance);

    private static ModServiceConfiguration CreateConfiguration()
    {
        return new ModServiceConfiguration
        {
            Http = new HttpApiConfiguration
            {
                ListenUrl = "http://127.0.0.1:5047"
            },
            Executor = new ExecutorConfiguration
            {
                Source = "repo",
                Asset = "NativeExecutor.dll"
            },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "repo",
                    Repo = "owner/repo",
                    Tag = "latest"
                }
            ]
        };
    }

    private async Task<TestApiHost> StartHostAsync(
        EffectiveConfigurationStore configurationStore,
        RuntimeStateStore runtimeState,
        SourceSyncService syncService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(configurationStore);
        builder.Services.AddSingleton(runtimeState);
        builder.Services.AddSingleton(syncService);

        var app = builder.Build();
        app.MapModServiceApi();
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("No server addresses were assigned.");
        var address = addresses.Single();

        var client = new HttpClient
        {
            BaseAddress = new Uri(address)
        };

        return new TestApiHost(app, client);
    }

    private sealed class TestOptionsMonitor(ModServiceConfiguration currentValue) : IOptionsMonitor<ModServiceConfiguration>
    {
        public ModServiceConfiguration CurrentValue { get; private set; } = currentValue;

        public ModServiceConfiguration Get(string? name)
            => CurrentValue;

        public IDisposable OnChange(Action<ModServiceConfiguration, string?> listener)
            => new Subscription();

        private sealed class Subscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class TestApiHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class FakeGitHubReleaseClient : IGitHubReleaseClient
    {
        private readonly Dictionary<string, Dictionary<string, FakeAsset>> _assets = new(StringComparer.OrdinalIgnoreCase);
        private long _nextId = 1;

        public void AddTextAsset(string sourceId, string assetName, string content)
            => AddBinaryAsset(sourceId, assetName, Encoding.UTF8.GetBytes(content));

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
