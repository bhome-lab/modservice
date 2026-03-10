namespace ModService.Host;

public sealed class ApplicationPaths
{
    public ApplicationPaths()
    {
        BaseDirectory = AppContext.BaseDirectory;
        ConfigPath = Path.Combine(BaseDirectory, "modservice.json");
        ProgramDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ModService");
    }

    public string BaseDirectory { get; }

    public string ConfigPath { get; }

    public string ProgramDataRoot { get; }
}
