using ModService.GitHub.Auth;

namespace ModService.Host;

public interface IGitHubCli
{
    Task<string> GetAuthTokenAsync(CancellationToken cancellationToken);
}

public sealed class GitHubTokenManager
{
    private readonly GitHubTokenStore _tokenStore;
    private readonly IGitHubCli _gitHubCli;

    public GitHubTokenManager(GitHubTokenStore tokenStore, IGitHubCli gitHubCli)
    {
        _tokenStore = tokenStore;
        _gitHubCli = gitHubCli;
    }

    public string FilePath => _tokenStore.FilePath;

    public bool HasToken()
        => _tokenStore.HasToken();

    public async Task SaveAsync(string token, CancellationToken cancellationToken)
    {
        var trimmed = token?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("GitHub token cannot be empty.");
        }

        await _tokenStore.SaveAsync(trimmed, cancellationToken);
    }

    public async Task ImportFromGhAsync(CancellationToken cancellationToken)
    {
        var token = await _gitHubCli.GetAuthTokenAsync(cancellationToken);
        await SaveAsync(token, cancellationToken);
    }

    public void Clear()
        => _tokenStore.Clear();
}
