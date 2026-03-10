using System.Runtime.Versioning;
using ModService.GitHub.Auth;

namespace ModService.Tests;

[SupportedOSPlatform("windows")]
public sealed class GitHubTokenStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceTokenTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveLoadAndClear_RoundTripsToken()
    {
        Directory.CreateDirectory(_root);
        var store = new GitHubTokenStore(Path.Combine(_root, "github-token.bin"));

        await store.SaveAsync("secret-token", CancellationToken.None);

        Assert.True(store.HasToken());
        Assert.Equal("secret-token", store.TryLoadToken());

        store.Clear();

        Assert.False(store.HasToken());
        Assert.Null(store.TryLoadToken());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
