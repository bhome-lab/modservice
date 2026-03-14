using System.Diagnostics;
using System.Windows.Forms;

namespace ModService.SetupLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var launcherPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to determine the current installer path.");

            using var setupPayload = ResolveSetupPayload(launcherPath);
            StopRunningInstances();
            return RunSetup(setupPayload.ExecutablePath, args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ModService Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static ExtractedSetupPayload ResolveSetupPayload(string launcherPath)
    {
        if (BundledSetupPayload.TryReadPayloadInfo(launcherPath, out _))
        {
            return BundledSetupPayload.ExtractToTempFile(launcherPath);
        }

        var launcherDirectory = Path.GetDirectoryName(launcherPath)
            ?? throw new InvalidOperationException("Unable to determine the installer directory.");
        var siblingSetupPath = Path.Combine(
            launcherDirectory,
            Path.GetFileNameWithoutExtension(launcherPath) + ".inner.exe");

        if (!File.Exists(siblingSetupPath))
        {
            throw new FileNotFoundException(
                "The embedded Velopack setup payload is missing.",
                siblingSetupPath);
        }

        return new ExtractedSetupPayload(siblingSetupPath, launcherDirectory, deleteOnDispose: false);
    }

    private static void StopRunningInstances()
    {
        foreach (var process in Process.GetProcessesByName("ModService.Host"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    if (process.CloseMainWindow() && process.WaitForExit(5_000))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(15_000);
                }
                catch
                {
                }
            }
        }
    }

    private static int RunSetup(string setupPath, IReadOnlyList<string> args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? AppContext.BaseDirectory
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("The bundled setup could not be started.");
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
