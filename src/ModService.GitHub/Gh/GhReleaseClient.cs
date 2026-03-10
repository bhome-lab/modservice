using System.Diagnostics;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Updates;

namespace ModService.GitHub.Gh;

public sealed class GhReleaseClient : IGitHubReleaseClient
{
    public async Task<IReadOnlyList<GitHubReleaseAsset>> GetReleaseAssetsAsync(SourceConfiguration source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = await RunGhAsync(
            $"release view {Quote(source.Tag)} --repo {Quote(source.Repo)} --json assets",
            cancellationToken);

        var document = JsonDocument.Parse(result.StandardOutput);
        var assets = new List<GitHubReleaseAsset>();
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            assets.Add(new GitHubReleaseAsset
            {
                Id = asset.GetProperty("id").GetInt64(),
                Name = asset.GetProperty("name").GetString() ?? string.Empty,
                Size = asset.GetProperty("size").GetInt64(),
                UpdatedAt = asset.GetProperty("updatedAt").GetDateTimeOffset()
            });
        }

        return assets;
    }

    public async Task<string> DownloadAssetAsync(
        SourceConfiguration source,
        GitHubReleaseAsset asset,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        await RunGhAsync(
            $"release download {Quote(source.Tag)} --repo {Quote(source.Repo)} --pattern {Quote(asset.Name)} --dir {Quote(destinationDirectory)} --clobber",
            cancellationToken);

        var filePath = Path.Combine(destinationDirectory, asset.Name);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Expected downloaded asset '{asset.Name}' at '{filePath}'.", filePath);
        }

        return filePath;
    }

    private static async Task<GhCommandResult> RunGhAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start gh process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh {arguments} failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");
        }

        return new GhCommandResult(stdout, stderr);
    }

    private static string Quote(string value)
        => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed record GhCommandResult(string StandardOutput, string StandardError);
}
