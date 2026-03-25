using System.Diagnostics;

namespace ModService.Tests;

internal static class NativeBuild
{
    private static readonly Lazy<bool> Build = new(BuildNative);

    public static void EnsureBuilt()
    {
        _ = Build.Value;
    }

    private static bool BuildNative()
    {
        if (!ShouldBuildNative())
        {
            return true;
        }

        var scriptPath = Path.Combine(RepoPaths.Root, "scripts", "build-native.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Configuration Debug",
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start native build process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Native build failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        return true;
    }

    private static bool ShouldBuildNative()
    {
        if (!File.Exists(RepoPaths.NativeExecutorDll) || !File.Exists(RepoPaths.SampleModuleDll) || !File.Exists(RepoPaths.DepModuleDll))
        {
            return true;
        }

        var outputTimestamp = new[]
        {
            File.GetLastWriteTimeUtc(RepoPaths.NativeExecutorDll),
            File.GetLastWriteTimeUtc(RepoPaths.SampleModuleDll),
            File.GetLastWriteTimeUtc(RepoPaths.DepModuleDll)
        }.Min();

        var nativeRoot = Path.Combine(RepoPaths.Root, "native");
        var latestSourceTimestamp = Directory.EnumerateFiles(nativeRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
            })
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return latestSourceTimestamp >= outputTimestamp;
    }
}
