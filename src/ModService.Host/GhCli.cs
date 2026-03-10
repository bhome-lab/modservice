using System.ComponentModel;
using System.Diagnostics;

namespace ModService.Host;

public sealed class GhCli : IGitHubCli
{
    public async Task<string> GetAuthTokenAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = "auth token",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start GitHub CLI.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "GitHub CLI did not return a token. Run `gh auth login` first or set the token manually."
                        : stderr);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException("GitHub CLI returned an empty token.");
            }

            return stdout;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "GitHub CLI 'gh' was not found. Install GitHub CLI or set the token manually.",
                exception);
        }
    }
}
