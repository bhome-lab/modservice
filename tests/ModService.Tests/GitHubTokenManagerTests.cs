using System.Runtime.Versioning;
using ModService.GitHub.Auth;
using ModService.Host;

namespace ModService.Tests;

[SupportedOSPlatform("windows")]
public sealed class GitHubTokenManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceTokenManagerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAsync_RejectsEmptyToken()
    {
        Directory.CreateDirectory(_root);
        var manager = new GitHubTokenManager(
            new GitHubTokenStore(Path.Combine(_root, "github-token.bin")),
            new FakeGitHubCli("unused"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task ImportFromGhAsync_SavesReturnedToken()
    {
        Directory.CreateDirectory(_root);
        var store = new GitHubTokenStore(Path.Combine(_root, "github-token.bin"));
        var manager = new GitHubTokenManager(store, new FakeGitHubCli("from-gh-token"));

        await manager.ImportFromGhAsync(CancellationToken.None);

        Assert.True(manager.HasToken());
        Assert.Equal("from-gh-token", store.TryLoadToken());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeGitHubCli(string token) : IGitHubCli
    {
        public Task<string> GetAuthTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult(token);
    }
}
