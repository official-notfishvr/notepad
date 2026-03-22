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
    private const int WordWrapSoftLimitCharacters = 300_000;
    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private readonly List<DocumentTab> _tabs = [];
    private int _activeTabIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(AppThemeMode.Dark);
        UpdateWindowButtons();
        AddNewTab();
        EditorTextBox.Focus();
    }

    public async Task OpenFileAsync(string path)
    {
        if (!await ConfirmDiscardChangesAsync(GetActiveTab()))
        {
            return;
        }

        try
        {
            await LoadFileIntoActiveTabAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FastNote", MessageBoxButton.OK, MessageBoxImage.Error);
            SetIdleState();
        }
    }

    private async Task LoadFileIntoActiveTabAsync(string path)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        tab.LoadVersion++;
        var loadVersion = tab.LoadVersion;
        tab.IsLoading = true;
        tab.LoadingLabel = "Loading...";
        _isInternalUpdate = true;
        EditorTextBox.Clear();
        tab.Path = path;
        tab.Text = string.Empty;
        tab.IsDirty = false;
        _isInternalUpdate = false;
        UpdateLoadingUi();
        UpdateTitle();
        RenderTabs();
        UpdateStatusBar();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[32 * 1024];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var chunk = new string(buffer, 0, read);
            builder.Append(chunk);
            tab.Text = builder.ToString();
            tab.LoadingLabel = $"Loading... {stream.Position * 100 / Math.Max(1, stream.Length):N0}%";

            if (GetActiveTab()?.Id == tab.Id && loadVersion == tab.LoadVersion)
            {
                _isInternalUpdate = true;
                EditorTextBox.Text = tab.Text;
                EditorTextBox.CaretIndex = Math.Min(EditorTextBox.CaretIndex, EditorTextBox.Text.Length);
                _isInternalUpdate = false;
                UpdateLoadingUi();
                UpdateStatusBar();
            }

            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.EncodingLabel = reader.CurrentEncoding.EncodingName.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            ? "UTF-8"
            : reader.CurrentEncoding.EncodingName;
        if (GetActiveTab()?.Id == tab.Id && loadVersion == tab.LoadVersion)
        {
            EncodingText.Text = tab.EncodingLabel;
            EditorTextBox.CaretIndex = 0;
            EditorTextBox.Select(0, 0);
            UpdateLoadingUi();
        }

        RenderTabs();
        UpdateTitle();
        UpdateStatusBar();
    }

    private async Task<bool> ConfirmDiscardChangesAsync(DocumentTab? tab)
    {
        if (tab is null || !tab.IsDirty)
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
            return await SaveDocumentAsync(tab, false);
        }

        return true;
    }

    private async Task<bool> SaveDocumentAsync(DocumentTab? tab, bool saveAs)
    {
        if (tab is null)
        {
            return false;
        }

        CaptureActiveTabState();

        var path = tab.Path;
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

        await File.WriteAllTextAsync(path!, tab.Text, new UTF8Encoding(false));
        tab.Path = path;
        tab.IsDirty = false;
        UpdateTitle();
        RenderTabs();
        UpdateStatusBar();
        return true;
    }

    private void UpdateTitle()
    {
        var tab = GetActiveTab();
        var displayName = tab is null || string.IsNullOrWhiteSpace(tab.Path) ? "Untitled" : Path.GetFileName(tab.Path);
        var dirtySuffix = tab?.IsDirty == true ? " •" : string.Empty;
        Title = $"{displayName}{dirtySuffix} - Notepad";
    }

    private void UpdateLoadingUi()
    {
        var tab = GetActiveTab();
        var isLoading = tab?.IsLoading == true;
        StreamingBadge.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = isLoading ? tab!.LoadingLabel : "Loading...";
        if (tab is not null)
        {
            EncodingText.Text = tab.EncodingLabel;
        }
    }

    private void UpdateStatusBar()
    {
        var lineIndex = EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex);
        var lineStart = EditorTextBox.GetCharacterIndexFromLineIndex(lineIndex);
        var column = EditorTextBox.CaretIndex - lineStart + 1;
        CaretText.Text = $"Ln {lineIndex + 1:N0}, Col {column:N0}";
        CharacterCountText.Text = $"{EditorTextBox.Text.Length:N0} characters";
        var tab = GetActiveTab();
        DocumentModeText.Text = tab?.WordWrapEnabled == true ? "Word wrap" : "Plain text";
        LineEndingText.Text = DetectLineEnding();
        WordWrapCheckGlyph.Opacity = tab?.WordWrapEnabled == true ? 0.8 : 0;
        StatusBarCheckGlyph.Opacity = _statusBarVisible ? 0.8 : 0;
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
        var enabled = GetActiveTab()?.WordWrapEnabled == true;
        EditorTextBox.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorTextBox.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
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

    private DocumentTab? GetActiveTab()
    {
        return _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
    }

    private void AddNewTab()
    {
        CaptureActiveTabState();
        var tab = new DocumentTab
        {
            Id = Guid.NewGuid(),
            Title = "Untitled",
            Text = string.Empty,
            EncodingLabel = "UTF-8"
        };
        _tabs.Add(tab);
        SwitchToTab(_tabs.Count - 1);
    }

    private void CaptureActiveTabState()
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        tab.Text = EditorTextBox.Text;
        tab.CaretIndex = EditorTextBox.CaretIndex;
        tab.SelectionStart = EditorTextBox.SelectionStart;
        tab.SelectionLength = EditorTextBox.SelectionLength;
        tab.WordWrapEnabled = tab.WordWrapEnabled;
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        CaptureActiveTabState();
        _activeTabIndex = index;
        var tab = _tabs[index];

        _isInternalUpdate = true;
        EditorTextBox.Text = tab.Text;
        EncodingText.Text = tab.EncodingLabel;
        _isInternalUpdate = false;

        ConfigureWordWrap();
        EditorTextBox.CaretIndex = Math.Min(tab.CaretIndex, EditorTextBox.Text.Length);
        EditorTextBox.Select(Math.Min(tab.SelectionStart, EditorTextBox.Text.Length), Math.Min(tab.SelectionLength, Math.Max(0, EditorTextBox.Text.Length - EditorTextBox.SelectionStart)));
        UpdateLoadingUi();
        UpdateTitle();
        RenderTabs();
        UpdateStatusBar();
        EditorTextBox.Focus();
    }

    private void RenderTabs()
    {
        TabStripPanel.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var isActive = i == _activeTabIndex;
            var border = new Border
            {
                Width = 244,
                Height = 30,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                Padding = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleButton = new Button
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock
                {
                    Text = tab.DisplayTitle,
                    Foreground = (Brush)FindResource("TabForegroundBrush"),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                Tag = i
            };
            titleButton.Click += TabButton_OnClick;

            var closeButton = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = tab.Id,
                Content = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("EditorMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            closeButton.Click += TabCloseButton_OnClick;
            Grid.SetColumn(closeButton, 1);

            grid.Children.Add(titleButton);
            grid.Children.Add(closeButton);
            border.Child = grid;
            TabStripPanel.Children.Add(border);
        }
    }

    private async Task CloseTabAsync(Guid tabId)
    {
        var index = _tabs.FindIndex(t => t.Id == tabId);
        if (index < 0)
        {
            return;
        }

        var tab = _tabs[index];
        if (index == _activeTabIndex)
        {
            CaptureActiveTabState();
        }

        if (!await ConfirmDiscardChangesAsync(tab))
        {
            return;
        }

        _tabs.RemoveAt(index);
        if (_tabs.Count == 0)
        {
            AddNewTab();
            return;
        }

        if (_activeTabIndex >= _tabs.Count)
        {
            _activeTabIndex = _tabs.Count - 1;
        }
        else if (index < _activeTabIndex)
        {
            _activeTabIndex--;
        }

        SwitchToTab(Math.Max(0, _activeTabIndex));
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
            await SaveDocumentAsync(GetActiveTab(), false);
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
        if (!await ConfirmDiscardChangesAsync(GetActiveTab()))
        {
            return;
        }

        _isInternalUpdate = true;
        EditorTextBox.Clear();
        var tab = GetActiveTab();
        if (tab is not null)
        {
            tab.Path = null;
            tab.Title = "Untitled";
            tab.Text = string.Empty;
            tab.IsDirty = false;
            tab.EncodingLabel = "UTF-8";
            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
        }
        EncodingText.Text = "UTF-8";
        _isInternalUpdate = false;
        UpdateLoadingUi();
        UpdateTitle();
        RenderTabs();
        UpdateStatusBar();
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();
    private async void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (!string.IsNullOrWhiteSpace(tab?.Path))
        {
            await OpenFileAsync(tab.Path!);
        }
    }

    private async void NewFileMenuItem_OnClick(object sender, RoutedEventArgs e) => await NewDocumentAsync();
    private async void SaveMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveDocumentAsync(GetActiveTab(), false);
    private async void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveDocumentAsync(GetActiveTab(), true);
    private async void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardChangesAsync(GetActiveTab()))
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
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        if (!tab.WordWrapEnabled && EditorTextBox.Text.Length > WordWrapSoftLimitCharacters)
        {
            MessageBox.Show(
                this,
                "Word wrap is disabled for very large documents because WPF reflows the entire buffer and can freeze the window.\n\nSave smaller excerpts or reduce file size before enabling wrap.",
                "FastNote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdateStatusBar();
            return;
        }

        tab.WordWrapEnabled = !tab.WordWrapEnabled;
        ConfigureWordWrap();
        UpdateStatusBar();
        ViewMenuPopup.IsOpen = false;
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
        _statusBarVisible = !_statusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusBar();
        ViewMenuPopup.IsOpen = false;
    }

    private void LightThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Light);
    private void DarkThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Dark);
    private void ThemeToggleToolbarButton_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(_themeMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark);

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF171520" : "#FFF3F1F8");
        SetBrush("ChromeBrush", dark ? "#FF160432" : "#FFEEE9FA");
        SetBrush("MenuBrush", dark ? "#FF241E3B" : "#FFF5F2FB");
        SetBrush("SurfaceBrush", dark ? "#FF2B2B2B" : "#FFFFFFFF");
        SetBrush("SurfaceRaisedBrush", dark ? "#FF343434" : "#FFF3F1F6");
        SetBrush("EditorBackgroundBrush", dark ? "#FF292929" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("EditorMutedBrush", dark ? "#FFA7A0B8" : "#FF706A7E");
        SetBrush("BorderBrush", dark ? "#FF3E3657" : "#FFD8D0E8");
        SetBrush("DividerBrush", dark ? "#FF4A425F" : "#FFE2DCEC");
        SetBrush("TabActiveBrush", dark ? "#FF272046" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#00000000" : "#00000000");
        SetBrush("TabForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("AccentBrush", dark ? "#FF7D68FF" : "#FF5E49D8");
        SetBrush("AccentSoftBrush", dark ? "#FF342B56" : "#FFE6E1FA");
        SetBrush("StatusBrush", dark ? "#FF241E3B" : "#FFF5F2FB");
        SetBrush("StatusForegroundBrush", dark ? "#FFE2DDF5" : "#FF4D475C");
        SetBrush("MenuForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("MenuSelectionBrush", dark ? "#FF3F365A" : "#FFE5DFF1");
        SetBrush("PopupBackgroundBrush", dark ? "#FF2F2F2F" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF3C3C3C" : "#FFD7D1E2");
        SetBrush("InputBackgroundBrush", dark ? "#FF343434" : "#FFF7F6FA");
        SetBrush("InputBorderBrush", dark ? "#FF5A5A5A" : "#FFCFC9DB");
        SetBrush("InputFocusBrush", dark ? "#FF8A78FF" : "#FF6A57E6");
        SetBrush("ButtonHoverBrush", dark ? "#FF4B4364" : "#FFE7E0F3");
        SetBrush("ButtonPressedBrush", dark ? "#FF5A5176" : "#FFD9D0EB");
        SetBrush("ScrollThumbBrush", dark ? "#FF6B657A" : "#FFC3BCD6");
        SetBrush("ScrollThumbHoverBrush", dark ? "#FF867F97" : "#FF9E95B7");
        SetBrush("ScrollTrackBrush", dark ? "#18211D2A" : "#10000000");
    }

    private static void SetBrush(string key, string hex)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void HeadingButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "# ", string.Empty);
    private void BulletListButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "- ", string.Empty);
    private void NumberedListButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "1. ", string.Empty);
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
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void NewTabButton_OnClick(object sender, RoutedEventArgs e) => AddNewTab();
    private async void CloseTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is not null)
        {
            await CloseTabAsync(tab.Id);
        }
    }
    private async void NewWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            System.Diagnostics.Process.Start(executable);
        }
        await Task.CompletedTask;
        FileMenuPopup.IsOpen = false;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowButtons();
    }

    private async void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardChangesAsync(GetActiveTab()))
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

    private void FileMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(FileMenuPopup);
    private void EditMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(EditMenuPopup);
    private void ViewMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(ViewMenuPopup);
    private void HeadingMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(HeadingPopup);
    private void ListMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(ListPopup);
    private void TableMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(TablePopup);

    private void TogglePopup(System.Windows.Controls.Primitives.Popup popup)
    {
        var shouldOpen = !popup.IsOpen;
        FileMenuPopup.IsOpen = false;
        EditMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
        HeadingPopup.IsOpen = false;
        ListPopup.IsOpen = false;
        TablePopup.IsOpen = false;
        popup.IsOpen = shouldOpen;
    }

    private void InsertTimeDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        InsertTextAtCaret(DateTime.Now.ToString("HH:mm M/d/yyyy"));
        EditMenuPopup.IsOpen = false;
    }

    private void FontMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Font picker is not implemented yet.", "FastNote");
        EditMenuPopup.IsOpen = false;
    }

    private void InsertTitleButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "# ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubtitleButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "## ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertHeadingButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubheadingButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "#### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSectionButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "##### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubsectionButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "###### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertBodyButton_OnClick(object sender, RoutedEventArgs e) { HeadingPopup.IsOpen = false; FileMenuPopup.IsOpen = false; }

    private void TabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SwitchToTab(index);
        }
    }

    private async void TabCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
        {
            await CloseTabAsync(id);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        CaptureActiveTabState();
        foreach (var tab in _tabs.ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    private void EditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalUpdate)
        {
            return;
        }

        var tab = GetActiveTab();
        if (tab is not null)
        {
            tab.Text = EditorTextBox.Text;
            tab.IsDirty = true;
            tab.Title = string.IsNullOrWhiteSpace(tab.Path) ? "Untitled" : Path.GetFileName(tab.Path);
        }
        UpdateTitle();
        RenderTabs();
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

    private void CloseFindPanelButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _replaceVisible = false;
        ReplaceTextBox.Visibility = Visibility.Collapsed;
        ReplaceButtonPanel.Visibility = Visibility.Collapsed;
        EditorTextBox.Focus();
    }

    private void ToggleReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        _replaceVisible = !_replaceVisible;
        ReplaceTextBox.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceButtonPanel.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_replaceVisible)
        {
            ReplaceTextBox.Focus();
        }
    }

    private void FindTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            FindNextInternal();
        }
    }

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e) => FindNextInternal();
    private void FindPreviousButton_OnClick(object sender, RoutedEventArgs e) => FindPreviousInternal();

    private void FindNextInternal()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var start = EditorTextBox.SelectionStart + EditorTextBox.SelectionLength;
        var index = EditorTextBox.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
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

    private void FindPreviousInternal()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var start = Math.Max(0, EditorTextBox.SelectionStart - 1);
        var index = EditorTextBox.Text.LastIndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = EditorTextBox.Text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
        }
    }

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (EditorTextBox.SelectionLength > 0 &&
            string.Equals(EditorTextBox.SelectedText, query, StringComparison.OrdinalIgnoreCase))
        {
            EditorTextBox.SelectedText = ReplaceTextBox.Text;
        }

        FindNextInternal();
    }

    private void ReplaceAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        EditorTextBox.Text = System.Text.RegularExpressions.Regex.Replace(
            EditorTextBox.Text,
            System.Text.RegularExpressions.Regex.Escape(query),
            ReplaceTextBox.Text,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private sealed class DocumentTab
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "Untitled";
        public string? Path { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool IsDirty { get; set; }
        public bool WordWrapEnabled { get; set; }
        public int CaretIndex { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public string EncodingLabel { get; set; } = "UTF-8";
        public bool IsLoading { get; set; }
        public string LoadingLabel { get; set; } = string.Empty;
        public int LoadVersion { get; set; }
        public string DisplayTitle => (string.IsNullOrWhiteSpace(Path) ? Title : System.IO.Path.GetFileName(Path)) + (IsDirty ? " •" : string.Empty);
    }
}
