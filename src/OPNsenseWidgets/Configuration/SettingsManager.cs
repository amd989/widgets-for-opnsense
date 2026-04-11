using System.Text.Json;

namespace OPNsenseWidgets.Configuration;

public static class SettingsManager
{
    // Use Desktop parent (non-virtualized by MSIX) so settings survive reinstall
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".opnsense-widgets");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static WidgetSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new WidgetSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<WidgetSettings>(json, JsonOptions)
                ?? new WidgetSettings();
        }
        catch
        {
            return new WidgetSettings();
        }
    }

    public static void Save(WidgetSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
