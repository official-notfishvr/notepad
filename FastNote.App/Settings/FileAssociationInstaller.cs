using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace FastNote.App.Settings;

public static class FileAssociationInstaller
{
    private const string ProgId = "FastNote.txtfile";
    private static readonly Uri EmbeddedIconUri = new("pack://application:,,,/Assets/txtfile.ico", UriKind.Absolute);
    private static readonly string[] SupportedExtensions = [".txt", ".log", ".md", ".json", ".xml", ".csv", ".ini", ".cfg", ".cs", ".py", ".js", ".ts", ".html", ".css"];

    public static string SupportedExtensionsLabel => "*.txt;*.log;*.md;*.json;*.xml;*.csv;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.html;*.css";

    public static string GetIconPath()
    {
        var iconDirectoryPath = Path.Combine(AppSettingsStore.SettingsDirectoryPath, "Assets");
        var iconPath = Path.Combine(iconDirectoryPath, "txtfile.ico");
        Directory.CreateDirectory(iconDirectoryPath);

        using var resourceStream = Application.GetResourceStream(EmbeddedIconUri)?.Stream;
        if (resourceStream is null)
        {
            return GetExecutablePath();
        }

        using var fileStream = new FileStream(iconPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        resourceStream.CopyTo(fileStream);
        return iconPath;
    }

    public static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("FastNote executable path could not be determined.");
    }

    public static bool IsTxtAssociationInstalledForCurrentApp()
    {
        try
        {
            using var commandKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            var expectedPath = GetExecutablePath();

            if (string.IsNullOrWhiteSpace(command) || !command.Contains(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var extension in SupportedExtensions)
            {
                using var extensionKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}");
                var extensionProgId = extensionKey?.GetValue(null) as string;
                if (!string.Equals(extensionProgId, ProgId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void InstallTxtAssociationForCurrentUser()
    {
        var exePath = GetExecutablePath();
        var escapedCommand = $"\"{exePath}\" \"%1\"";
        var iconValue = $"\"{GetIconPath()}\"";

        using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progIdKey?.SetValue(null, "FastNote Text Document");
        }

        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            iconKey?.SetValue(null, iconValue);
        }

        using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            commandKey?.SetValue(null, escapedCommand);
        }

        foreach (var extension in SupportedExtensions)
        {
            using var extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
            extensionKey?.SetValue(null, ProgId);
        }

        using (var appCommandKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\FastNote.exe\shell\open\command"))
        {
            appCommandKey?.SetValue(null, escapedCommand);
        }

        NotifyShellOfAssociationChange();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);

    private static void NotifyShellOfAssociationChange()
    {
        const uint shcneAssocChanged = 0x08000000;
        const uint shcnfIdList = 0x0000;
        SHChangeNotify(shcneAssocChanged, shcnfIdList, 0, 0);
    }
}
