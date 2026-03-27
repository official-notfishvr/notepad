using System.IO;
using System.Text.Json;

namespace FastNote.App.Settings;

public sealed class SessionTabState
{
    public string Title { get; set; } = "Untitled.txt";
    public string? Path { get; set; }
    public string? DraftFileName { get; set; }
    public bool IsDirty { get; set; }
    public bool WordWrapEnabled { get; set; }
    public int CaretIndex { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public string EncodingKey { get; set; } = "utf-8";
    public string EncodingLabel { get; set; } = "UTF-8";
    public string LineEndingKey { get; set; } = "crlf";
    public string LineEndingLabel { get; set; } = "Windows (CRLF)";
    public bool IsMarkdownPreviewEnabled { get; set; }
}

public sealed class SessionState
{
    public int ActiveTabIndex { get; set; }
    public List<SessionTabState> Tabs { get; set; } = [];
}

public static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SessionDirectoryPath => Path.Combine(AppSettingsStore.SettingsDirectoryPath, "Session");

    public static string SessionFilePath => Path.Combine(SessionDirectoryPath, "session.json");

    public static void Save(SessionState session)
    {
        Directory.CreateDirectory(SessionDirectoryPath);
        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(SessionFilePath, json);
    }

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(SessionFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(SessionFilePath);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string GetDraftPath(string draftFileName)
    {
        return Path.Combine(SessionDirectoryPath, draftFileName);
    }

    public static void ClearMissingDrafts(HashSet<string> retainedFileNames)
    {
        if (!Directory.Exists(SessionDirectoryPath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(SessionDirectoryPath, "*.draft"))
        {
            var fileName = Path.GetFileName(path);
            if (retainedFileNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch { }
        }
    }
}
