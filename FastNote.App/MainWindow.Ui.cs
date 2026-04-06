using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FastNote.App.Settings;

namespace FastNote.App;

public partial class MainWindow
{
    private const string ShellName = "FastNote";

    private static AppThemeMode GetThemeModeFromSettings(string? theme)
    {
        return string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    private void ApplySavedSettings()
    {
        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        _statusBarVisible = _appSettings.StatusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        _editorFontFamily = new FontFamily(string.IsNullOrWhiteSpace(_appSettings.EditorFontFamily) ? "Segoe UI Variable Text" : _appSettings.EditorFontFamily);
        _editorFontStyle = string.Equals(_appSettings.EditorFontStyle, "Italic", StringComparison.OrdinalIgnoreCase) ? FontStyles.Italic : FontStyles.Normal;
        _editorFontWeight = string.Equals(_appSettings.EditorFontWeight, "Bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal;

        _editor.FontFamily = _editorFontFamily;
        _editor.FontStyle = _editorFontStyle;
        _editor.FontWeight = _editorFontWeight;
        _editor.FontSize = Math.Clamp(_appSettings.EditorFontSize, 6, 72);

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
        _appSettings.StatusBarVisible = _statusBarVisible;
        _appSettings.EditorFontFamily = _editorFontFamily.Source;
        _appSettings.EditorFontStyle = _editorFontStyle == FontStyles.Italic ? "Italic" : "Normal";
        _appSettings.EditorFontWeight = _editorFontWeight == FontWeights.Bold ? "Bold" : "Normal";
        _appSettings.EditorFontSize = _editor.FontSize;
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

        _appSettings.Theme = dialog.SelectedTheme;
        _appSettings.StatusBarVisible = dialog.ShowStatusBar;
        _appSettings.DefaultWordWrap = dialog.DefaultWordWrap;
        _appSettings.RestorePreviousSession = dialog.RestorePreviousSession;
        _appSettings.EnableFileOpenCache = dialog.EnableFileOpenCache;
        _editorFontFamily = dialog.SelectedFontFamily;
        _editorFontStyle = dialog.SelectedFontStyle;
        _editorFontWeight = dialog.SelectedFontWeight;

        _editor.FontFamily = _editorFontFamily;
        _editor.FontStyle = _editorFontStyle;
        _editor.FontWeight = _editorFontWeight;
        _editor.FontSize = dialog.SelectedFontSize;

        _statusBarVisible = _appSettings.StatusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        UpdateStatusBar();
        SaveSettings();
    }

    private void UpdateTitle()
    {
        var tab = GetActiveTab();
        var displayName = GetDisplayName(tab);
        var dirtySuffix = tab?.IsDirty == true ? " •" : string.Empty;
        Title = $"{displayName}{dirtySuffix} - {ShellName}";
    }

    private void UpdateLoadingUi()
    {
        var tab = GetActiveTab();
        var isLoading = tab?.IsLoading == true;
        StreamingBadge.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = isLoading ? tab!.LoadingLabel : "Loading…";
        EncodingText.Text = tab?.EncodingLabel ?? "UTF-8";
        UpdateSpellCheckState(tab);
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
            var document = _editor.Document;
            var documentLength = document?.TextLength ?? 0;
            var caretOffset = Math.Clamp(_editor.CaretOffset, 0, documentLength);
            var line = _editor.GetLineByOffset(documentLength == 0 ? 0 : caretOffset);
            var lineIndex = Math.Max(0, (line?.LineNumber ?? 1) - 1);
            var column = line is null ? 1 : caretOffset - line.Value.Offset + 1;

            CaretText.Text = $"Ln {Math.Max(1, lineIndex + 1):N0}, Col {Math.Max(1, column):N0}";

            if (_editor.SelectionLength > 0)
            {
                SelectionText.Text = $"{_editor.SelectionLength:N0} characters selected";
                SelectionText.Visibility = Visibility.Visible;
                SelectionDivider.Visibility = Visibility.Visible;
            }
            else
            {
                SelectionText.Visibility = Visibility.Collapsed;
                SelectionDivider.Visibility = Visibility.Collapsed;
            }
        }

        CharacterCountText.Text = $"{tab?.LoadedCharacterCount ?? (_editor.Document?.TextLength ?? 0):N0} chars";
        LineEndingText.Text = tab?.CanShowEncodingAndLineEndings == true ? tab.LineEndingLabel : "Windows (CRLF)";
        EncodingText.Text = tab?.CanShowEncodingAndLineEndings == true ? tab.EncodingLabel : "UTF-8";

        UpdateZoomStatus();
    }

    private void ConfigureWordWrap()
    {
        var tab = GetActiveTab();
        var enabled = tab?.WordWrapEnabled == true;

        _editor.WordWrap = enabled;
        _editor.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateZoomStatus()
    {
        var percent = _editor.FontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
    }

    private void ZoomBy(int delta)
    {
        var nextSize = Math.Clamp(_editor.FontSize + delta, 6, 72);
        _editor.FontSize = nextSize;
        UpdateZoomStatus();
        SaveSettings();
    }

    private void InsertTextAtCaret(string value)
    {
        var index = _editor.CaretOffset;
        _editor.InsertText(index, value);
        _editor.CaretOffset = index + value.Length;
    }

    private void ReplaceTextRange(int start, int length, string replacement)
    {
        _editor.ReplaceText(start, length, replacement);
        _editor.Select(start, replacement.Length);
        _editor.CaretOffset = start + replacement.Length;
        _editor.Focus();
    }

    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        EditMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
        HeadingMenuPopup.IsOpen = false;
        ListMenuPopup.IsOpen = false;
    }

    private void ApplyHybridShellLayout()
    {
        FindPanel.Margin = new Thickness(68, 12, 68, 0);
        FindPanel.Background = GetResourceBrush("PopupBackgroundBrush", Colors.Transparent);
        FindPanel.BorderThickness = new Thickness(1);
        FindPanel.CornerRadius = new CornerRadius(12);
        FindPanel.VerticalAlignment = VerticalAlignment.Top;
        FindPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        FindPanel.Width = double.NaN;
        FindPanel.MaxWidth = 920;

        UpdateFindPanelControls();
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

        SetBrush("WindowBackgroundBrush", dark ? "#FF202020" : "#FFF7F7F7");
        SetBrush("ChromeBrush", dark ? "#FF272727" : "#FFF2F2F2");
        SetBrush("MenuBrush", dark ? "#FF272727" : "#FFF2F2F2");
        SetBrush("SurfaceBrush", dark ? "#FF2B2B2B" : "#FFFFFFFF");
        SetBrush("SurfaceRaisedBrush", dark ? "#FF323232" : "#FFF3F3F3");
        SetBrush("EditorBackgroundBrush", dark ? "#FF1F1F1F" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFF5F5F5" : "#FF1A1A1A");
        SetBrush("EditorMutedBrush", dark ? "#FFABABAB" : "#FF5F5F5F");
        SetBrush("BorderBrush", dark ? "#FF3B3B3B" : "#FFD6D6D6");
        SetBrush("DividerBrush", dark ? "#FF404040" : "#FFD2D2D2");
        SetBrush("TabActiveBrush", dark ? "#FF1F1F1F" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#FF2A2A2A" : "#00FFFFFF");
        SetBrush("TabHoverBrush", dark ? "#FF343434" : "#FFEAEAEA");
        SetBrush("TabForegroundBrush", dark ? "#FFF4F4F4" : "#FF1A1A1A");
        SetBrush("TabInactiveForegroundBrush", dark ? "#FFC7C7C7" : "#FF5B5B5B");
        SetBrush("AccentBrush", dark ? "#FF76B9FF" : "#FF0F6CBD");
        SetBrush("AccentSoftBrush", dark ? "#2976B9FF" : "#1F0F6CBD");
        SetBrush("StatusBrush", dark ? "#FF262626" : "#FFF1F1F1");
        SetBrush("StatusForegroundBrush", dark ? "#FFBCBCBC" : "#FF444444");
        SetBrush("MenuForegroundBrush", dark ? "#FFF5F5F5" : "#FF1A1A1A");
        SetBrush("MenuSelectionBrush", dark ? "#FF393939" : "#FFE7E7E7");
        SetBrush("PopupBackgroundBrush", dark ? "#FF2B2B2B" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF454545" : "#FFD6D6D6");
        SetBrush("InputBackgroundBrush", dark ? "#FF303030" : "#FFFFFFFF");
        SetBrush("InputBorderBrush", dark ? "#FF555555" : "#FFC8C8C8");
        SetBrush("InputFocusBrush", dark ? "#FF8CC6FF" : "#FF0F6CBD");
        SetBrush("ButtonHoverBrush", dark ? "#FF3B3B3B" : "#FFE6E6E6");
        SetBrush("ButtonPressedBrush", dark ? "#FF454545" : "#FFD9D9D9");
        SetBrush("ScrollThumbBrush", dark ? "#FF6B6B6B" : "#FFB0B0B0");
        SetBrush("ScrollThumbHoverBrush", dark ? "#FF8B8B8B" : "#FF8E8E8E");
        SetBrush("ScrollTrackBrush", dark ? "#14FFFFFF" : "#14000000");
        SetBrush("FindHighlightBrush", dark ? "#FF8CC6FF" : "#FF0F6CBD");
        SetBrush("FindHighlightForegroundBrush", dark ? "#FF0F1115" : "#FFFFFFFF");
        SetBrush("AppIconBackgroundBrush", dark ? "#FF0B6CBD" : "#FF0F6CBD");
        var activeTab = GetActiveTab();
        if (activeTab is not null)
        {
            activeTab.MarkdownPreviewCacheKey = null;
            ApplySyntaxHighlighting(activeTab);
        }
        RenderTabs();
        _editor.Redraw();
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
            var document = new FlowDocument(new Paragraph(new Run(_editor.Text)))
            {
                FontFamily = _editor.FontFamily,
                FontSize = _editor.FontSize,
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
        _editor.Undo();
    }

    private void RedoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        _editor.Redo();
    }

    private void CutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        _editor.Cut();
    }

    private void CopyMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        _editor.Copy();
    }

    private void PasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        _editor.Paste();
    }

    private void DeleteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        _editor.SelectedText = string.Empty;
    }

    private void SearchWithBingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        var query = _editor.SelectedText;
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
        _editor.SelectAll();
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
        _editor.FontSize = DefaultEditorFontSize;
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
            _editor.Focus();
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

        var dialog = new FontPickerDialog(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontSize) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            _editor.FontFamily = dialog.SelectedFontFamily;
            _editor.FontStyle = dialog.SelectedFontStyle;
            _editor.FontWeight = dialog.SelectedFontWeight;
            _editor.FontSize = dialog.SelectedFontSize;
            _editor.InvalidateVisual();
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
        tab.Kind = DocumentKind.Markdown;
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
        FileMenuPopup.PlacementTarget = FileMenuButton;
        TogglePopup(FileMenuPopup);
    }

    private void EditMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.PlacementTarget = EditMenuButton;
        TogglePopup(EditMenuPopup);
    }

    private void ViewMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.PlacementTarget = ViewMenuButton;
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

    private void EditorTextBox_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editor.TryGetOffsetFromPoint(e.GetPosition(_editor.View), out var offset))
        {
            return;
        }

        if (offset < _editor.SelectionStart || offset > _editor.SelectionStart + _editor.SelectionLength)
        {
            _editor.Select(offset, 0);
        }

        _editor.CaretOffset = offset;
        _editor.Focus();
    }

    private void EditorTextBox_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _editor.ContextMenu = CreateEditorContextMenu();
    }

    private ContextMenu CreateEditorContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TabContextMenuStyle") };
        var document = _editor.Document;
        var hasSuggestionItems = false;

        if (document is not null && _spellCheckColorizer.IsEnabled && _spellCheckColorizer.TryGetSpellingIssue(document, _editor.CaretOffset, out var issue))
        {
            foreach (var suggestion in issue.Suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
            {
                var suggestionItem = new MenuItem { Header = suggestion, Style = (Style)FindResource("TabContextMenuItemStyle") };
                suggestionItem.Click += (_, _) => ReplaceTextRange(issue.Start, issue.Length, suggestion);
                menu.Items.Add(suggestionItem);
                hasSuggestionItems = true;
            }

            if (!hasSuggestionItems)
            {
                menu.Items.Add(
                    new MenuItem
                    {
                        Header = "No suggestions",
                        IsEnabled = false,
                        Style = (Style)FindResource("TabContextMenuItemStyle"),
                    }
                );
            }

            var ignoreItem = new MenuItem { Header = $"Ignore \"{issue.Word}\"", Style = (Style)FindResource("TabContextMenuItemStyle") };
            ignoreItem.Click += (_, _) =>
            {
                _spellCheckColorizer.IgnoreWord(issue.Word);
                _editor.Redraw();
            };
            menu.Items.Add(ignoreItem);

            var addItem = new MenuItem { Header = $"Add \"{issue.Word}\"", Style = (Style)FindResource("TabContextMenuItemStyle") };
            addItem.Click += (_, _) =>
            {
                _spellCheckColorizer.AddWordToDictionary(issue.Word);
                _editor.Redraw();
            };
            menu.Items.Add(addItem);
            menu.Items.Add(new Separator { Style = (Style)FindResource("TabContextSeparatorStyle") });
        }

        menu.Items.Add(CreateEditorMenuItem("Cut", () => _editor.Cut(), CanCutEditorSelection()));
        menu.Items.Add(CreateEditorMenuItem("Copy", () => _editor.Copy(), CanCopyEditorSelection()));
        menu.Items.Add(CreateEditorMenuItem("Paste", () => _editor.Paste(), CanPasteIntoEditor()));
        menu.Items.Add(new Separator { Style = (Style)FindResource("TabContextSeparatorStyle") });
        menu.Items.Add(CreateEditorMenuItem("Select all", () => _editor.SelectAll(), true));
        return menu;
    }

    private bool CanCutEditorSelection()
    {
        return !_editor.IsReadOnly && _editor.SelectionLength > 0;
    }

    private bool CanCopyEditorSelection()
    {
        return _editor.SelectionLength > 0;
    }

    private bool CanPasteIntoEditor()
    {
        if (_editor.IsReadOnly)
        {
            return false;
        }

        try
        {
            return Clipboard.ContainsText();
        }
        catch
        {
            return true;
        }
    }

    private MenuItem CreateEditorMenuItem(string header, Action action, bool isEnabled)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            Style = (Style)FindResource("TabContextMenuItemStyle"),
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void TabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SwitchToTab(index);
        }
    }

    private ContextMenu CreateTabContextMenu(Guid tabId)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TabContextMenuStyle"), Tag = tabId };

        menu.Items.Add(CreateTabMenuItem("Close", TabContextClose_OnClick, tabId));
        menu.Items.Add(CreateTabMenuItem("Close others", TabContextCloseOthers_OnClick, tabId));
        menu.Items.Add(CreateTabMenuItem("Close tabs to the right", TabContextCloseTabsToRight_OnClick, tabId));
        menu.Items.Add(new Separator { Style = (Style)FindResource("TabContextSeparatorStyle") });
        menu.Items.Add(CreateTabMenuItem("Reload", TabContextReload_OnClick, tabId));
        menu.Items.Add(new Separator { Style = (Style)FindResource("TabContextSeparatorStyle") });
        menu.Items.Add(CreateTabMenuItem("Open file location", TabContextOpenFileLocation_OnClick, tabId));
        menu.Items.Add(CreateTabMenuItem("Copy file path", TabContextCopyFilePath_OnClick, tabId));
        return menu;
    }

    private MenuItem CreateTabMenuItem(string header, RoutedEventHandler onClick, Guid tabId)
    {
        var item = new MenuItem
        {
            Header = header,
            Tag = tabId,
            Style = (Style)FindResource("TabContextMenuItemStyle"),
        };
        item.Click += onClick;
        return item;
    }

    private void TabBorder_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Guid tabId, ContextMenu: ContextMenu menu })
        {
            return;
        }

        var index = _tabs.FindIndex(tab => tab.Id == tabId);
        if (index >= 0)
        {
            SwitchToTab(index);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void TabCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid tabId })
        {
            await CloseTabAsync(tabId);
        }
    }

    private async void TabContextClose_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Guid tabId })
        {
            await CloseTabAsync(tabId);
        }
    }

    private async void TabContextCloseOthers_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Guid tabId })
        {
            await CloseOtherTabsAsync(tabId);
        }
    }

    private async void TabContextCloseTabsToRight_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Guid tabId })
        {
            await CloseTabsToRightAsync(tabId);
        }
    }

    private async void TabContextReload_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Guid tabId })
        {
            return;
        }

        var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null || string.IsNullOrWhiteSpace(tab.Path))
        {
            return;
        }

        var index = _tabs.FindIndex(item => item.Id == tabId);
        if (index >= 0)
        {
            SwitchToTab(index);
        }

        await StartLoadingIntoTabAsync(tab, tab.Path);
    }

    private void TabContextOpenFileLocation_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Guid tabId })
        {
            return;
        }

        var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
        if (string.IsNullOrWhiteSpace(tab?.Path) || !File.Exists(tab.Path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tab.Path}\"") { UseShellExecute = true });
    }

    private void TabContextCopyFilePath_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Guid tabId })
        {
            return;
        }

        var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
        if (!string.IsNullOrWhiteSpace(tab?.Path))
        {
            Clipboard.SetText(tab.Path);
        }
    }

    private void CloseFindPanelButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _replaceVisible = false;
        UpdateFindPanelControls();
        ResetFindHighlight();
        RemoveHighlightAdorner();
        _editor.Focus();
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

    private static string GetDisplayName(DocumentTab? tab)
    {
        if (tab is null)
        {
            return "Untitled.txt";
        }

        if (!string.IsNullOrWhiteSpace(tab.Path))
        {
            return Path.GetFileName(tab.Path);
        }

        return string.IsNullOrWhiteSpace(tab.Title) ? "Untitled.txt" : tab.Title;
    }
}
