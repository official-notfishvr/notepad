using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private string? _currentPath;
    private bool _isDirty;
    private bool _isInternalUpdate;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(AppThemeMode.Dark);
        ConfigureWordWrap();
        UpdateTitle();
        UpdateWindowButtons();
        UpdateStatusBar();
        EditorTextBox.Focus();
    }

    public async Task OpenFileAsync(string path)
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        try
        {
            await LoadFileIntoEditorAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FastNote", MessageBoxButton.OK, MessageBoxImage.Error);
            SetIdleState();
        }
    }

    private async Task LoadFileIntoEditorAsync(string path)
    {
        _isInternalUpdate = true;
        EditorTextBox.Clear();
        _currentPath = path;
        _isDirty = false;
        UpdateTitle();
        UpdateStatusBar();

        StreamingBadge.Visibility = Visibility.Visible;
        LoadingText.Text = "Loading...";

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[32 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            EditorTextBox.AppendText(new string(buffer, 0, read));
            LoadingText.Text = $"Loading... {stream.Position * 100 / Math.Max(1, stream.Length):N0}%";
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        StreamingBadge.Visibility = Visibility.Collapsed;
        EncodingText.Text = reader.CurrentEncoding.EncodingName.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            ? "UTF-8"
            : reader.CurrentEncoding.EncodingName;
        _isInternalUpdate = false;
        EditorTextBox.CaretIndex = 0;
        EditorTextBox.Select(0, 0);
        UpdateStatusBar();
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!_isDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "Do you want to save changes to this file?",
            "FastNote",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            return await SaveCurrentDocumentAsync(false);
        }

        return true;
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool saveAs)
    {
        var path = _currentPath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Text files|*.txt;*.log;*.md;*.json;*.xml|All files|*.*",
                FileName = string.IsNullOrWhiteSpace(path) ? "Untitled.txt" : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        await File.WriteAllTextAsync(path!, EditorTextBox.Text, new UTF8Encoding(false));
        _currentPath = path;
        _isDirty = false;
        UpdateTitle();
        UpdateStatusBar();
        return true;
    }

    private void UpdateTitle()
    {
        var displayName = string.IsNullOrWhiteSpace(_currentPath) ? "Untitled" : Path.GetFileName(_currentPath);
        var dirtySuffix = _isDirty ? " •" : string.Empty;
        Title = $"{displayName}{dirtySuffix} - Notepad";
        TabTitleText.Text = displayName + dirtySuffix;
    }

    private void UpdateStatusBar()
    {
        var lineIndex = EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex);
        var lineStart = EditorTextBox.GetCharacterIndexFromLineIndex(lineIndex);
        var column = EditorTextBox.CaretIndex - lineStart + 1;
        CaretText.Text = $"Ln {lineIndex + 1:N0}, Col {column:N0}";
        CharacterCountText.Text = $"{EditorTextBox.Text.Length:N0} characters";
        DocumentModeText.Text = WordWrapMenuItem.IsChecked ? "Word wrap" : "Plain text";
        LineEndingText.Text = DetectLineEnding();
        UpdateZoomStatus();
    }

    private string DetectLineEnding()
    {
        if (EditorTextBox.Text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "Windows (CRLF)";
        }

        if (EditorTextBox.Text.Contains('\n'))
        {
            return "Unix (LF)";
        }

        return "Windows (CRLF)";
    }

    private void ConfigureWordWrap()
    {
        EditorTextBox.TextWrapping = WordWrapMenuItem.IsChecked ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorTextBox.HorizontalScrollBarVisibility = WordWrapMenuItem.IsChecked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private static void WrapSelection(TextBox textBox, string prefix, string suffix)
    {
        if (textBox.SelectionLength > 0)
        {
            var start = textBox.SelectionStart;
            var length = textBox.SelectionLength;
            var content = textBox.SelectedText;
            textBox.SelectedText = prefix + content + suffix;
            textBox.Select(start + prefix.Length, length);
            return;
        }

        var lineIndex = textBox.GetLineIndexFromCharacterIndex(textBox.CaretIndex);
        var lineStart = textBox.GetCharacterIndexFromLineIndex(lineIndex);
        textBox.Select(lineStart, 0);
        textBox.SelectedText = prefix;
        textBox.CaretIndex = lineStart + prefix.Length;
    }

    private void InsertTextAtCaret(string value)
    {
        var index = EditorTextBox.CaretIndex;
        EditorTextBox.Text = EditorTextBox.Text.Insert(index, value);
        EditorTextBox.CaretIndex = index + value.Length;
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            await NewDocumentAsync();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            await OpenWithDialogAsync();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveCurrentDocumentAsync(false);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Add)
        {
            e.Handled = true;
            ZoomBy(1);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Subtract)
        {
            e.Handled = true;
            ZoomBy(-1);
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files|*.txt;*.log;*.md;*.json;*.xml|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async Task NewDocumentAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        _isInternalUpdate = true;
        EditorTextBox.Clear();
        _currentPath = null;
        _isDirty = false;
        EncodingText.Text = "UTF-8";
        _isInternalUpdate = false;
        UpdateTitle();
        UpdateStatusBar();
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();
    private async void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentPath))
        {
            await OpenFileAsync(_currentPath);
        }
    }

    private async void NewFileMenuItem_OnClick(object sender, RoutedEventArgs e) => await NewDocumentAsync();
    private async void SaveMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveCurrentDocumentAsync(false);
    private async void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveCurrentDocumentAsync(true);
    private async void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardChangesAsync())
        {
            Close();
        }
    }

    private void UndoMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.Undo();
    private void CutMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.Cut();
    private void CopyMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.Copy();
    private void PasteMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.Paste();
    private void SelectAllMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.SelectAll();

    private void WordWrapMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ConfigureWordWrap();
        UpdateStatusBar();
    }

    private void ZoomBy(int delta)
    {
        EditorTextBox.FontSize = Math.Clamp(EditorTextBox.FontSize + delta, 10, 30);
        UpdateZoomStatus();
    }

    private void ZoomInMenuItem_OnClick(object sender, RoutedEventArgs e) => ZoomBy(1);
    private void ZoomOutMenuItem_OnClick(object sender, RoutedEventArgs e) => ZoomBy(-1);
    private void RestoreZoomMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditorTextBox.FontSize = DefaultEditorFontSize;
        UpdateZoomStatus();
    }

    private void UpdateZoomStatus()
    {
        var percent = EditorTextBox.FontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
    }

    private void StatusBarMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        StatusChrome.Visibility = StatusBarMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LightThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Light);
    private void DarkThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Dark);
    private void ThemeToggleToolbarButton_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(_themeMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark);

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF201A29" : "#FFF2F2F4");
        SetBrush("ChromeBrush", dark ? "#FF170534" : "#FFEDE8FA");
        SetBrush("MenuBrush", dark ? "#FF2A2147" : "#FFF5F0FF");
        SetBrush("EditorBackgroundBrush", dark ? "#FF292929" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFF1F1F1" : "#FF1F1F1F");
        SetBrush("EditorMutedBrush", dark ? "#FFA9A1BC" : "#FF696969");
        SetBrush("BorderBrush", dark ? "#FF3A3155" : "#FFD9D2E8");
        SetBrush("TabActiveBrush", dark ? "#FF2A2147" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#00000000" : "#00000000");
        SetBrush("TabForegroundBrush", dark ? "#FFF1F1F1" : "#FF1F1F1F");
        SetBrush("AccentBrush", dark ? "#FF7D68FF" : "#FF5A43E6");
        SetBrush("StatusBrush", dark ? "#FF2A2147" : "#FFF5F0FF");
        SetBrush("StatusForegroundBrush", dark ? "#FFE2DDF5" : "#FF4A4A4A");
        SetBrush("MenuForegroundBrush", dark ? "#FFF1F1F1" : "#FF1F1F1F");
        SetBrush("MenuSelectionBrush", dark ? "#FF41355F" : "#FFEAEAEA");

        LightThemeMenuItem.IsChecked = !dark;
        DarkThemeMenuItem.IsChecked = dark;
    }

    private static void SetBrush(string key, string hex)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void HeadingButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "# ", string.Empty);
    private void BulletListButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "- ", string.Empty);
    private void ChecklistButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "[ ] ", string.Empty);
    private void BoldButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "**", "**");
    private void ItalicButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "_", "_");
    private void StrikeButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "~~", "~~");
    private void LinkButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "[", "](https://)");
    private void TableButton_OnClick(object sender, RoutedEventArgs e) => InsertTextAtCaret("| Column 1 | Column 2 |\r\n| --- | --- |\r\n| Value | Value |\r\n");
    private void ClearFormattingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (EditorTextBox.SelectionLength > 0)
        {
            var cleaned = EditorTextBox.SelectedText.Replace("**", string.Empty).Replace("_", string.Empty).Replace("~~", string.Empty);
            EditorTextBox.SelectedText = cleaned;
        }
    }

    private void FindButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = Microsoft.VisualBasic.Interaction.InputBox("Find text:", "Find", string.Empty);
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var index = EditorTextBox.Text.IndexOf(query, EditorTextBox.CaretIndex, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = EditorTextBox.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
        }
    }

    private async void NewTabButton_OnClick(object sender, RoutedEventArgs e) => await NewDocumentAsync();
    private async void CloseTabButton_OnClick(object sender, RoutedEventArgs e) => await NewDocumentAsync();

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowButtons();
    }

    private async void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardChangesAsync())
        {
            Close();
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaxRestoreButton_OnClick(sender, e);
            return;
        }

        DragMove();
    }

    private void Window_OnStateChanged(object? sender, EventArgs e) => UpdateWindowButtons();

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void EditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalUpdate)
        {
            return;
        }

        _isDirty = true;
        UpdateTitle();
        UpdateStatusBar();
    }

    private void EditorTextBox_OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateStatusBar();

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await OpenFileAsync(files[0]);
        }
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SetIdleState()
    {
        StreamingBadge.Visibility = Visibility.Collapsed;
        UpdateStatusBar();
    }

    private enum AppThemeMode
    {
        Light,
        Dark
    }
}
