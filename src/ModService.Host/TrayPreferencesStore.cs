using System.Text.Json;

namespace ModService.Host;

public sealed class TrayPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private TrayPreferences _current;

    public TrayPreferencesStore(ApplicationPaths paths)
    {
        _filePath = Path.Combine(paths.ProgramDataRoot, "tray-settings.json");
        _current = Load();
    }

    public bool AreProcessNotificationsEnabled()
    {
        lock (_gate)
        {
            return _current.ProcessNotificationsEnabled;
        }
    }

    public void SetProcessNotificationsEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_current.ProcessNotificationsEnabled == enabled)
            {
                return;
            }

            _current = _current with
            {
                ProcessNotificationsEnabled = enabled
            };

            Save(_current);
        }
    }

    private TrayPreferences Load()
    {
        if (!File.Exists(_filePath))
        {
            return new TrayPreferences();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TrayPreferences>(json, JsonOptions) ?? new TrayPreferences();
        }
        catch
        {
            return new TrayPreferences();
        }
    }

    private void Save(TrayPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Tray preferences path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(preferences, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}

public sealed record TrayPreferences
{
    public bool ProcessNotificationsEnabled { get; init; } = true;
}
