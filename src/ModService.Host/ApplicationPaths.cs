namespace ModService.Host;

public sealed class ApplicationPaths
{
    public ApplicationPaths()
    {
        BaseDirectory = AppContext.BaseDirectory;
        UserDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModService");
        ConfigPath = Path.Combine(UserDataRoot, "modservice.json");
        ProgramDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ModService");
        LogsDirectory = Path.Combine(ProgramDataRoot, "logs");
        SampleConfigPath = Path.Combine(BaseDirectory, "modservice.sample.json");

        Directory.CreateDirectory(UserDataRoot);
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(LogsDirectory);
        EnsureConfigExists();
    }

    public string BaseDirectory { get; }

    public string UserDataRoot { get; }

    public string ConfigPath { get; }

    public string ProgramDataRoot { get; }

    public string LogsDirectory { get; }

    public string SampleConfigPath { get; }

    private void EnsureConfigExists()
    {
        if (File.Exists(ConfigPath))
        {
            return;
        }

        var candidates = new[]
        {
            Path.Combine(BaseDirectory, "modservice.json"),
            SampleConfigPath
        };

        var seedPath = candidates.FirstOrDefault(File.Exists);
        if (seedPath is null)
        {
            return;
        }

        File.Copy(seedPath, ConfigPath, overwrite: false);
    }
}
