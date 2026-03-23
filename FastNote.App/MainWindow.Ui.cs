using FastNote.App.Settings;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow
{
    private static AppThemeMode GetThemeModeFromSettings(string? theme)
    {
        return string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    private static AppAppearanceMode GetAppearanceModeFromSettings(string? appearanceMode)
    {
        return string.Equals(appearanceMode, "Windows11", StringComparison.OrdinalIgnoreCase) ? AppAppearanceMode.Windows11 : AppAppearanceMode.Classic;
    }

    private void ApplySavedSettings()
    {
        ApplyAppearanceMode(GetAppearanceModeFromSettings(_appSettings.AppearanceMode));
        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        _statusBarVisible = _appSettings.StatusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        _editorFontFamily = new FontFamily(string.IsNullOrWhiteSpace(_appSettings.EditorFontFamily) ? "Segoe UI Variable Text" : _appSettings.EditorFontFamily);
        _editorFontStyle = string.Equals(_appSettings.EditorFontStyle, "Italic", StringComparison.OrdinalIgnoreCase) ? FontStyles.Italic : FontStyles.Normal;
        _editorFontWeight = string.Equals(_appSettings.EditorFontWeight, "Bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal;

        EditorTextBox.FontFamily = _editorFontFamily;
        EditorTextBox.FontStyle = _editorFontStyle;
        EditorTextBox.FontWeight = _editorFontWeight;
        EditorTextBox.FontSize = Math.Clamp(_appSettings.EditorFontSize, 6, 72);

        var tab = GetActiveTab();
        if (tab is not null)
        {
            tab.WordWrapEnabled = _appSettings.DefaultWordWrap;
            ConfigureWordWrap();
        }

        UpdateStatusBar();
    }

    private void SaveSettings()
    {
        _appSettings.Theme = _themeMode == AppThemeMode.Light ? "Light" : "Dark";
        _appSettings.AppearanceMode = _appearanceMode == AppAppearanceMode.Windows11 ? "Windows11" : "Classic";
        _appSettings.StatusBarVisible = _statusBarVisible;
        _appSettings.EditorFontFamily = _editorFontFamily.Source;
        _appSettings.EditorFontStyle = _editorFontStyle == FontStyles.Italic ? "Italic" : "Normal";
        _appSettings.EditorFontWeight = _editorFontWeight == FontWeights.Bold ? "Bold" : "Normal";
        _appSettings.EditorFontSize = EditorTextBox.FontSize;
        _appSettings.RecentFiles = _recentFiles.ToList();

        AppSettingsStore.Save(_appSettings);
    }

    private void ShowSettingsWindow()
    {
        CloseMenus();
        var dialog = new SettingsWindow(_appSettings, FileAssociationInstaller.IsTxtAssociationInstalledForCurrentApp()) { Owner = this };
        var result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        _appSettings.AppearanceMode = dialog.SelectedAppearanceMode;
        _appSettings.Theme = dialog.SelectedTheme;
        _appSettings.StatusBarVisible = dialog.ShowStatusBar;
        _appSettings.DefaultWordWrap = dialog.DefaultWordWrap;
        _appSettings.RestorePreviousSession = dialog.RestorePreviousSession;

        ApplyAppearanceMode(GetAppearanceModeFromSettings(_appSettings.AppearanceMode));
        _statusBarVisible = _appSettings.StatusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        UpdateStatusBar();
        SaveSettings();
    }

    private void UpdateTitle()
    {
        var tab = GetActiveTab();
        var displayName = tab is null || string.IsNullOrWhiteSpace(tab.Path) ? tab?.Title ?? "Untitled" : Path.GetFileName(tab.Path);
        var dirtySuffix = tab?.IsDirty == true ? " •" : string.Empty;
        Title = $"{displayName}{dirtySuffix} - Notepad";
    }

    private void UpdateLoadingUi()
    {
        var tab = GetActiveTab();
        var isLoading = tab?.IsLoading == true;
        StreamingBadge.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = isLoading ? tab!.LoadingLabel : "Loading…";
        EncodingText.Text = tab?.EncodingLabel ?? "UTF-8";
    }

    private void UpdateStatusBar()
    {
        var tab = GetActiveTab();
        WordWrapCheckGlyph.Opacity = tab?.WordWrapEnabled == true ? 0.85 : 0;
        StatusBarCheckGlyph.Opacity = _statusBarVisible ? 0.85 : 0;
        UpdateMarkdownUi(tab);

        if (_themeMode == AppThemeMode.Light)
        {
            LightThemeGlyph.Opacity = 0.85;
            DarkThemeGlyph.Opacity = 0;
        }
        else
        {
            LightThemeGlyph.Opacity = 0;
            DarkThemeGlyph.Opacity = 0.85;
        }

        if (IsMarkdownPreviewActive(tab))
        {
            CaretText.Text = "Markdown preview";
            SelectionText.Visibility = Visibility.Collapsed;
            SelectionDivider.Visibility = Visibility.Collapsed;
        }
        else
        {
            var document = EditorTextBox.Document;
            var documentLength = document?.TextLength ?? 0;
            var caretOffset = Math.Clamp(EditorTextBox.CaretOffset, 0, documentLength);
            var line = document is null ? null : document.GetLineByOffset(documentLength == 0 ? 0 : caretOffset);
            var lineIndex = Math.Max(0, (line?.LineNumber ?? 1) - 1);
            var column = line is null ? 1 : caretOffset - line.Offset + 1;

            CaretText.Text = $"Ln {Math.Max(1, lineIndex + 1):N0}, Col {Math.Max(1, column):N0}";

            if (EditorTextBox.SelectionLength > 0)
            {
                SelectionText.Text = $"{EditorTextBox.SelectionLength:N0} characters selected";
                SelectionText.Visibility = Visibility.Visible;
                SelectionDivider.Visibility = Visibility.Visible;
            }
            else
            {
                SelectionText.Visibility = Visibility.Collapsed;
                SelectionDivider.Visibility = Visibility.Collapsed;
            }
        }

        CharacterCountText.Text = $"{tab?.LoadedCharacterCount ?? (EditorTextBox.Document?.TextLength ?? 0):N0} characters";
        LineEndingText.Text = tab?.LineEndingLabel ?? "Windows (CRLF)";
        EncodingText.Text = tab?.EncodingLabel ?? "UTF-8";

        UpdateZoomStatus();
    }

    private void ConfigureWordWrap()
    {
        var tab = GetActiveTab();
        var enabled = tab?.WordWrapEnabled == true;

        EditorTextBox.WordWrap = enabled;
        EditorTextBox.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateZoomStatus()
    {
        var percent = EditorTextBox.FontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
    }

    private void ZoomBy(int delta)
    {
        var nextSize = Math.Clamp(EditorTextBox.FontSize + delta, 6, 72);
        EditorTextBox.FontSize = nextSize;
        UpdateZoomStatus();
        SaveSettings();
    }

    private void InsertTextAtCaret(string value)
    {
        var index = EditorTextBox.CaretOffset;
        EditorTextBox.Document.Insert(index, value);
        EditorTextBox.CaretOffset = index + value.Length;
    }

    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        EditMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
        HeadingMenuPopup.IsOpen = false;
        ListMenuPopup.IsOpen = false;
    }

    private void ApplyAppearanceMode(AppAppearanceMode appearanceMode)
    {
        _appearanceMode = appearanceMode;
        var isWindows11 = appearanceMode == AppAppearanceMode.Windows11;

        ClassicMenuRow.Visibility = isWindows11 ? Visibility.Collapsed : Visibility.Visible;
        Windows11MenuRow.Visibility = isWindows11 ? Visibility.Visible : Visibility.Collapsed;

        FindPanel.Margin = new Thickness(68, 8, 68, 10);
        FindPanel.Background = GetResourceBrush("PopupBackgroundBrush", Colors.Transparent);
        FindPanel.BorderThickness = new Thickness(1);
        FindPanel.CornerRadius = new CornerRadius(12);
        FindPanel.VerticalAlignment = VerticalAlignment.Top;
        FindPanel.HorizontalAlignment = HorizontalAlignment.Center;
        FindPanel.Width = 860;

        UpdateFindPanelControls();
        RenderTabs();
        UpdateStatusBar();
    }

    private void UpdateFindPanelControls()
    {
        FindOptionsButton.Visibility = Visibility.Visible;
        FindExpandButton.Visibility = Visibility.Visible;
        FindOptionsRow.Visibility = _findOptionsVisible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceRowPanel.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
        FindExpandButton.ToolTip = _replaceVisible ? "Hide replace" : "Show replace";
        FindExpandButton.Content = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 10,
            Text = _replaceVisible ? "\uE70E" : "\uE70D",
        };
    }

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF1C1C1C" : "#FFF3F3F3");
        SetBrush("ChromeBrush", dark ? (_appearanceMode == AppAppearanceMode.Windows11 ? "#FF19003A" : "#FF282828") : "#FFE9E9E9");
        SetBrush("MenuBrush", dark ? (_appearanceMode == AppAppearanceMode.Windows11 ? "#FF281C49" : "#FF282828") : "#FFE9E9E9");
        SetBrush("SurfaceBrush", dark ? "#FF282828" : "#FFFFFFFF");
        SetBrush("SurfaceRaisedBrush", dark ? "#FF323232" : "#FFF3F3F3");
        SetBrush("EditorBackgroundBrush", dark ? "#FF1C1C1C" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFFCFCFC" : "#FF1A1A1A");
        SetBrush("EditorMutedBrush", dark ? "#FF8A8A8A" : "#FF6C6C6C");
        SetBrush("BorderBrush", dark ? "#FF3A3A3A" : "#FFCCCCCC");
        SetBrush("DividerBrush", dark ? "#FF3A3A3A" : "#FFCCCCCC");
        SetBrush("TabActiveBrush", dark ? "#FF1C1C1C" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#00000000" : "#00000000");
        SetBrush("TabHoverBrush", dark ? "#FF2A2A2A" : "#FFE4E4E4");
        SetBrush("TabForegroundBrush", dark ? "#FFFCFCFC" : "#FF1A1A1A");
        SetBrush("TabInactiveForegroundBrush", dark ? "#FFB0B0B0" : "#FF6C6C6C");
        SetBrush("AccentBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("AccentSoftBrush", dark ? "#1A60CDFF" : "#1A0067C0");
        SetBrush("StatusBrush", dark ? "#FF282828" : "#FFE9E9E9");
        SetBrush("StatusForegroundBrush", dark ? "#FFB0B0B0" : "#FF404040");
        SetBrush("MenuForegroundBrush", dark ? "#FFFCFCFC" : "#FF1A1A1A");
        SetBrush("MenuSelectionBrush", dark ? "#FF383838" : "#FFE0E0E0");
        SetBrush("PopupBackgroundBrush", dark ? "#FF2C2C2C" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF484848" : "#FFCCCCCC");
        SetBrush("InputBackgroundBrush", dark ? "#FF333333" : "#FFFFFFFF");
        SetBrush("InputBorderBrush", dark ? "#FF545454" : "#FFAAAAAA");
        SetBrush("InputFocusBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("ButtonHoverBrush", dark ? "#FF383838" : "#FFE0E0E0");
        SetBrush("ButtonPressedBrush", dark ? "#FF444444" : "#FFD0D0D0");
        SetBrush("ScrollThumbBrush", dark ? "#FF606060" : "#FFAAAAAA");
        SetBrush("ScrollThumbHoverBrush", dark ? "#FF808080" : "#FF888888");
        SetBrush("ScrollTrackBrush", dark ? "#14FFFFFF" : "#14000000");
        SetBrush("FindHighlightBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("FindHighlightForegroundBrush", dark ? "#FF000000" : "#FFFFFFFF");
        SetBrush("AppIconBackgroundBrush", dark ? "#FF1F56D8" : "#FF0F6CBD");
        var activeTab = GetActiveTab();
        if (activeTab is not null)
        {
            activeTab.MarkdownPreviewCacheKey = null;
        }
        RenderTabs();
        EditorTextBox.TextArea.TextView.Redraw();
        RefreshMarkdownPreview(activeTab);
    }

    private void RebuildRecentFilesMenu()
    {
        RecentFilesPanel.Children.Clear();
        RecentFilesExpander.Visibility = _recentFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in _recentFiles)
        {
            var button = new Button
            {
                Style = (Style)FindResource("PopupRowButtonStyle"),
                Tag = path,
                Content = new TextBlock
                {
                    Text = path,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 210,
                },
            };

            button.Click += RecentFileButton_OnClick;
            RecentFilesPanel.Children.Add(button);
        }
    }

    private void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _recentFiles.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > 10)
        {
            _recentFiles.RemoveRange(10, _recentFiles.Count - 10);
        }

        RebuildRecentFilesMenu();
        SaveSettings();
    }

    private void SetBrush(string key, string hexColor)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));

        if (Resources.Contains(key))
        {
            Resources[key] = brush;
        }

        if (Application.Current.Resources.Contains(key))
        {
            Application.Current.Resources[key] = brush;
        }
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();

    private async void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (!string.IsNullOrWhiteSpace(tab?.Path))
        {
            await StartLoadingIntoTabAsync(tab, tab.Path!);
        }

        FileMenuPopup.IsOpen = false;
    }

    private async void NewFileMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        await NewDocumentAsync();
    }

    private async void SaveMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        await SaveDocumentAsync(GetActiveTab(), saveAs: false);
    }

    private async void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        await SaveDocumentAsync(GetActiveTab(), saveAs: true);
    }

    private async void SaveWithEncodingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        await SaveDocumentAsync(GetActiveTab(), saveAs: false, chooseOptions: true);
    }

    private async void SaveAllMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        foreach (var tab in _tabs.ToArray())
        {
            if (tab.IsDirty && !await SaveDocumentAsync(tab, saveAs: false))
            {
                break;
            }
        }
    }

    private async void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await TryCloseWindowAsync();
    }

    private void PageSetupMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        MessageBox.Show(this, "Page setup is not available in this build.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PrintMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() == true)
        {
            var document = new FlowDocument(new Paragraph(new Run(EditorTextBox.Text)))
            {
                FontFamily = EditorTextBox.FontFamily,
                FontSize = EditorTextBox.FontSize,
                PageWidth = printDialog.PrintableAreaWidth,
                PagePadding = new Thickness(60),
                ColumnGap = 0,
                ColumnWidth = printDialog.PrintableAreaWidth,
            };

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            printDialog.PrintDocument(paginator, Title);
        }
    }

    private void UndoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Undo();
    }

    private void RedoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Redo();
    }

    private void CutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Cut();
    }

    private void CopyMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Copy();
    }

    private void PasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Paste();
    }

    private void DeleteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.SelectedText = string.Empty;
    }

    private void SearchWithBingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        var query = string.IsNullOrWhiteSpace(EditorTextBox.SelectedText) ? EditorTextBox.TextArea.Selection.GetText() : EditorTextBox.SelectedText;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}") { UseShellExecute = true });
    }

    private void ClearFormattingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        ClearMarkdownFormatting();
    }

    private void SelectAllMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.SelectAll();
    }

    private void WordWrapMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        ViewMenuPopup.IsOpen = false;
        tab.WordWrapEnabled = !tab.WordWrapEnabled;
        ConfigureWordWrap();
        UpdateStatusBar();
    }

    private void ZoomInMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ZoomBy(2);
    }

    private void ZoomOutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ZoomBy(-2);
    }

    private void RestoreZoomMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        EditorTextBox.FontSize = DefaultEditorFontSize;
        UpdateZoomStatus();
        SaveSettings();
    }

    private void StatusBarMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _statusBarVisible = !_statusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusBar();
        ViewMenuPopup.IsOpen = false;
        SaveSettings();
    }

    private void LightThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ApplyTheme(AppThemeMode.Light);
        UpdateStatusBar();
        SaveSettings();
    }

    private void DarkThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ApplyTheme(AppThemeMode.Dark);
        UpdateStatusBar();
        SaveSettings();
    }

    private void ThemeToggleToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void MarkdownPreviewMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        var tab = GetActiveTab();
        if (!SupportsMarkdownPreview(tab) || tab is null)
        {
            return;
        }

        tab.IsMarkdownPreviewEnabled = !tab.IsMarkdownPreviewEnabled;
        UpdateEditorSurface(tab);
        UpdateStatusBar();

        if (tab.IsMarkdownPreviewEnabled)
        {
            RefreshMarkdownPreview(tab);
        }
        else
        {
            EditorTextBox.Focus();
        }
    }

    private void InsertTimeDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        InsertTextAtCaret(DateTime.Now.ToString("h:mm tt M/d/yyyy"));
    }

    private void FontMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        var dialog = new FontPickerDialog(EditorTextBox.FontFamily, EditorTextBox.FontStyle, EditorTextBox.FontWeight, EditorTextBox.FontSize) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            EditorTextBox.FontFamily = dialog.SelectedFontFamily;
            EditorTextBox.FontStyle = dialog.SelectedFontStyle;
            EditorTextBox.FontWeight = dialog.SelectedFontWeight;
            EditorTextBox.FontSize = dialog.SelectedFontSize;
            EditorTextBox.TextArea.TextView.InvalidateVisual();
            _editorFontFamily = dialog.SelectedFontFamily;
            _editorFontStyle = dialog.SelectedFontStyle;
            _editorFontWeight = dialog.SelectedFontWeight;
            UpdateZoomStatus();
            SaveSettings();
        }
    }

    private void SettingsMenuItem_OnClick(object sender, RoutedEventArgs e) => ShowSettingsWindow();

    private void NewTabButton_OnClick(object sender, RoutedEventArgs e) => CreateNewTabAndActivate();

    private async void NewWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        }

        await Task.CompletedTask;
    }

    private async void NewMarkdownMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        CreateNewTabAndActivate();
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        tab.Title = "Untitled.md";
        tab.IsMarkdownPreviewEnabled = false;
        RenderTabs();
        UpdateTitle();
        await Task.CompletedTask;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowButtons();
    }

    private async void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        _skipCloseConfirmation = true;
        Close();
    }

    private async Task TryCloseWindowAsync()
    {
        foreach (var tab in _tabs.ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                return;
            }
        }

        _skipCloseConfirmation = true;
        Close();
    }

    private async void CloseTabMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        var activeTab = GetActiveTab();
        if (activeTab is not null)
        {
            await CloseTabAsync(activeTab.Id);
        }
    }

    private async void CloseWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        CloseWindowButton_OnClick(sender, e);
        await Task.CompletedTask;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            MaxRestoreButton_OnClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        DragMove();
        e.Handled = true;
    }

    private void Window_OnStateChanged(object? sender, EventArgs e) => UpdateWindowButtons();

    private void FileMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.PlacementTarget = _appearanceMode == AppAppearanceMode.Windows11 ? FileMenuButtonWindows11 : FileMenuButton;
        TogglePopup(FileMenuPopup);
    }

    private void EditMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.PlacementTarget = _appearanceMode == AppAppearanceMode.Windows11 ? EditMenuButtonWindows11 : EditMenuButton;
        TogglePopup(EditMenuPopup);
    }

    private void ViewMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.PlacementTarget = _appearanceMode == AppAppearanceMode.Windows11 ? ViewMenuButtonWindows11 : ViewMenuButton;
        TogglePopup(ViewMenuPopup);
    }

    private void TogglePopup(System.Windows.Controls.Primitives.Popup popup)
    {
        var shouldOpen = !popup.IsOpen;
        CloseMenus();
        HeadingMenuPopup.IsOpen = false;
        ListMenuPopup.IsOpen = false;
        popup.IsOpen = shouldOpen;
    }

    private void TabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SwitchToTab(index);
        }
    }

    private async void TabCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid tabId })
        {
            await CloseTabAsync(tabId);
        }
    }

    private void CloseFindPanelButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _replaceVisible = false;
        UpdateFindPanelControls();
        ResetFindHighlight();
        RemoveHighlightAdorner();
        EditorTextBox.Focus();
    }

    private void FindPanelOptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _replaceVisible = !_replaceVisible;
        UpdateFindPanelControls();

        if (_replaceVisible)
        {
            ReplaceTextBox.Focus();
            ReplaceTextBox.SelectAll();
            return;
        }

        FindTextBox.Focus();
    }

    private void FindPanelMoreOptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _findOptionsVisible = !_findOptionsVisible;
        UpdateFindPanelControls();
    }

    private async void RecentFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (sender is Button { Tag: string path } && File.Exists(path))
        {
            await OpenFileAsync(path);
        }
    }
}
