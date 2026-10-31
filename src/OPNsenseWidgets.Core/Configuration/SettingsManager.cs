using System.Text.Json;

namespace OPNsenseWidgets.Configuration;

public sealed class SettingsManager : ISettingsManager
{
    // Use Desktop parent (non-virtualized by MSIX) so settings survive reinstall
    private readonly string _settingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".opnsense-widgets");

    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsManager()
    {
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
    }

    public WidgetSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new WidgetSettings();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<WidgetSettings>(json, JsonOptions)
                ?? new WidgetSettings();
        }
        catch
        {
            return new WidgetSettings();
        }
    }

    public void Save(WidgetSettings settings)
    {
        Directory.CreateDirectory(_settingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
