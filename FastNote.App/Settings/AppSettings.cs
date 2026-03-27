using System.IO;
using System.Text.Json;

namespace FastNote.App.Settings;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = CurrentSettingsVersion;
    public const int CurrentSettingsVersion = 2;
    public string Theme { get; set; } = "Dark";
    public bool StatusBarVisible { get; set; } = true;
    public bool RestorePreviousSession { get; set; } = true;
    public bool DefaultWordWrap { get; set; }
    public string EditorFontFamily { get; set; } = "Segoe UI Variable Text";
    public string EditorFontStyle { get; set; } = "Normal";
    public string EditorFontWeight { get; set; } = "Normal";
    public double EditorFontSize { get; set; } = 14;
    public List<string> RecentFiles { get; set; } = [];
    public bool SetupCompleted { get; set; }
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsDirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastNote");

    public static string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (settings.SettingsVersion <= 0)
            {
                settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectoryPath);
        settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }
}
