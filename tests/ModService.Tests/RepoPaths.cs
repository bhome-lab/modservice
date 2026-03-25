namespace ModService.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRepoRoot();

    public static string NativeExecutorDll => Path.Combine(Root, "artifacts", "native", "NativeExecutor", "x64", "Debug", "NativeExecutor.dll");

    public static string SampleModuleDll => Path.Combine(Root, "artifacts", "native", "SampleModule", "x64", "Debug", "SampleModule.dll");

    public static string DepModuleDll => Path.Combine(Root, "artifacts", "native", "DepModule", "x64", "Debug", "DepModule.dll");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "plan.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
