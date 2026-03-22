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
        if (tab?.Mode == DocumentMode.LargePreview)
        {
            CaretText.Text = $"Ln {Math.Max(1, PreviewViewport.TopLine + 1):N0}, Col 1";
            SelectionText.Visibility = Visibility.Collapsed;
            SelectionDivider.Visibility = Visibility.Collapsed;
            LineEndingText.Text = tab.LineEndingLabel;
            EncodingText.Text = tab.EncodingLabel;
            WordWrapCheckGlyph.Opacity = 0;
            StatusBarCheckGlyph.Opacity = _statusBarVisible ? 0.85 : 0;

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

            UpdateZoomStatus();
            return;
        }

        var lineIndex = EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex);
        var lineStart = EditorTextBox.GetCharacterIndexFromLineIndex(lineIndex);
        var column = EditorTextBox.CaretIndex - lineStart + 1;
        var displayLine = Math.Max(1, lineIndex + 1 + (tab?.IsPartialEdit == true ? (int)tab.PartialEditStartLine : 0));

        CaretText.Text = $"Ln {displayLine:N0}, Col {Math.Max(1, column):N0}";

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

        LineEndingText.Text = tab?.LineEndingLabel ?? DetectLineEnding(EditorTextBox.Text);
        EncodingText.Text = tab?.EncodingLabel ?? "UTF-8";

        WordWrapCheckGlyph.Opacity = tab?.WordWrapEnabled == true ? 0.85 : 0;
        StatusBarCheckGlyph.Opacity = _statusBarVisible ? 0.85 : 0;

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

        UpdateZoomStatus();
    }

    private void ConfigureWordWrap()
    {
        var enabled = GetActiveTab()?.WordWrapEnabled == true;
        EditorTextBox.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorTextBox.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PreviewViewport.WrapText = false;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateZoomStatus()
    {
        var fontSize = IsPreviewSurfaceActive() ? PreviewViewport.EditorFontSize : EditorTextBox.FontSize;
        var percent = fontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
    }

    private void ZoomBy(int delta)
    {
        var baseSize = IsPreviewSurfaceActive() ? PreviewViewport.EditorFontSize : EditorTextBox.FontSize;
        var nextSize = Math.Clamp(baseSize + delta, 6, 72);
        EditorTextBox.FontSize = nextSize;
        PreviewViewport.EditorFontSize = nextSize;
        UpdateZoomStatus();
    }

    private void InsertTextAtCaret(string value)
    {
        var tab = GetActiveTab();
        if (tab?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        var index = EditorTextBox.CaretIndex;
        EditorTextBox.Text = EditorTextBox.Text.Insert(index, value);
        EditorTextBox.CaretIndex = index + value.Length;
    }

    private void ShowLargeFileEditingMessage()
    {
        MessageBox.Show(
            this,
            "This file is extremely large, so it is shown in read-only preview mode.",
            "Notepad",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        EditMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
    }

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF202020" : "#FFF3F3F3");
        SetBrush("ChromeBrush", dark ? "#FF2C2C2C" : "#FFE9E9E9");
        SetBrush("MenuBrush", dark ? "#FF2C2C2C" : "#FFE9E9E9");
        SetBrush("SurfaceBrush", dark ? "#FF2C2C2C" : "#FFFFFFFF");
        SetBrush("SurfaceRaisedBrush", dark ? "#FF383838" : "#FFF3F3F3");
        SetBrush("EditorBackgroundBrush", dark ? "#FF1F1F1F" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFFFFFFF" : "#FF000000");
        SetBrush("EditorMutedBrush", dark ? "#FF9D9D9D" : "#FF6C6C6C");
        SetBrush("BorderBrush", dark ? "#FF383838" : "#FFCCCCCC");
        SetBrush("DividerBrush", dark ? "#FF404040" : "#FFCCCCCC");
        SetBrush("TabActiveBrush", dark ? "#FF1F1F1F" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#00000000" : "#00000000");
        SetBrush("TabHoverBrush", dark ? "#FF2D2D2D" : "#FFE8E8E8");
        SetBrush("TabForegroundBrush", dark ? "#FFFFFFFF" : "#FF000000");
        SetBrush("AccentBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("AccentSoftBrush", dark ? "#2260CDFF" : "#220067C0");
        SetBrush("StatusBrush", dark ? "#FF2C2C2C" : "#FFE9E9E9");
        SetBrush("StatusForegroundBrush", dark ? "#FFB4B4B4" : "#FF404040");
        SetBrush("MenuForegroundBrush", dark ? "#FFFFFFFF" : "#FF000000");
        SetBrush("MenuSelectionBrush", dark ? "#FF3D3D3D" : "#FFE0E0E0");
        SetBrush("PopupBackgroundBrush", dark ? "#FF2C2C2C" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF454545" : "#FFCCCCCC");
        SetBrush("InputBackgroundBrush", dark ? "#FF383838" : "#FFFFFFFF");
        SetBrush("InputBorderBrush", dark ? "#FF5A5A5A" : "#FFAAAAAA");
        SetBrush("InputFocusBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("ButtonHoverBrush", dark ? "#FF3D3D3D" : "#FFE0E0E0");
        SetBrush("ButtonPressedBrush", dark ? "#FF484848" : "#FFD0D0D0");
        SetBrush("ScrollThumbBrush", dark ? "#FF686868" : "#FFAAAAAA");
        SetBrush("ScrollThumbHoverBrush", dark ? "#FF888888" : "#FF888888");
        SetBrush("ScrollTrackBrush", dark ? "#18FFFFFF" : "#18000000");
        SetBrush("FindHighlightBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("FindHighlightForegroundBrush", dark ? "#FF000000" : "#FFFFFFFF");
    }

    private void SetBrush(string key, string hexColor)
    {
        if (Application.Current.Resources.Contains(key))
        {
            Application.Current.Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hexColor));
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

    private async void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var tab in _tabs.ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                return;
            }
        }

        Close();
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
                ColumnWidth = printDialog.PrintableAreaWidth
            };

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            printDialog.PrintDocument(paginator, Title);
        }
    }

    private async void UndoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditorTextBox.Undo();
    }

    private async void RedoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditorTextBox.Redo();
    }

    private async void CutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditorTextBox.Cut();
    }

    private void CopyMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.Copy();
    }

    private async void PasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditorTextBox.Paste();
    }

    private void SelectAllMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        EditorTextBox.SelectAll();
    }

    private async void WordWrapMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        ViewMenuPopup.IsOpen = false;

        if (!await EnsureEditableTabAsync(tab))
        {
            return;
        }

        if (!tab.WordWrapEnabled && EditorTextBox.Text.Length > WordWrapSoftLimitCharacters)
        {
            MessageBox.Show(this, "Word wrap may be slow for very large documents.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

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
        PreviewViewport.EditorFontSize = DefaultEditorFontSize;
        UpdateZoomStatus();
    }

    private void StatusBarMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _statusBarVisible = !_statusBarVisible;
        StatusChrome.Visibility = _statusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusBar();
        ViewMenuPopup.IsOpen = false;
    }

    private void LightThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ApplyTheme(AppThemeMode.Light);
        UpdateStatusBar();
    }

    private void DarkThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = false;
        ApplyTheme(AppThemeMode.Dark);
        UpdateStatusBar();
    }

    private void ThemeToggleToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_themeMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark);
        UpdateStatusBar();
    }

    private async void InsertTimeDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        InsertTextAtCaret(DateTime.Now.ToString("h:mm tt M/d/yyyy"));
    }

    private void FontMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        var dialog = new FontPickerDialog(
            EditorTextBox.FontFamily,
            EditorTextBox.FontStyle,
            EditorTextBox.FontWeight,
            EditorTextBox.FontSize)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            EditorTextBox.FontFamily = dialog.SelectedFontFamily;
            EditorTextBox.FontStyle = dialog.SelectedFontStyle;
            EditorTextBox.FontWeight = dialog.SelectedFontWeight;
            EditorTextBox.FontSize = dialog.SelectedFontSize;
            _editorFontFamily = dialog.SelectedFontFamily;
            _editorFontStyle = dialog.SelectedFontStyle;
            _editorFontWeight = dialog.SelectedFontWeight;
            UpdateZoomStatus();
        }
    }

    private void NewTabButton_OnClick(object sender, RoutedEventArgs e) => CreateNewTabAndActivate();

    private async void NewWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        FileMenuPopup.IsOpen = false;
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            Process.Start(Environment.ProcessPath!);
        }

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
        foreach (var tab in _tabs.ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                return;
            }
        }

        Close();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

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

    private void TogglePopup(System.Windows.Controls.Primitives.Popup popup)
    {
        var shouldOpen = !popup.IsOpen;
        CloseMenus();
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
        ReplaceRowPanel.Visibility = Visibility.Collapsed;
        ResetFindHighlight();
        EditorTextBox.Focus();
    }
}
