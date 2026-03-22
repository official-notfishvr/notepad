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

        var document = EditorTextBox.Document;
        var documentLength = document?.TextLength ?? 0;
        var caretOffset = Math.Clamp(EditorTextBox.CaretOffset, 0, documentLength);
        var line = document is null
            ? null
            : document.GetLineByOffset(documentLength == 0 ? 0 : caretOffset);
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
        DocumentViewportControl.WrapText = enabled;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateZoomStatus()
    {
        var percent = EditorTextBox.FontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
        DocumentViewportControl.EditorFontSize = EditorTextBox.FontSize;
    }

    private void ZoomBy(int delta)
    {
        var nextSize = Math.Clamp(EditorTextBox.FontSize + delta, 6, 72);
        EditorTextBox.FontSize = nextSize;
        UpdateZoomStatus();
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
    }

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF1C1C1C" : "#FFF3F3F3");
        SetBrush("ChromeBrush", dark ? "#FF282828" : "#FFE9E9E9");
        SetBrush("MenuBrush", dark ? "#FF282828" : "#FFE9E9E9");
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

    private void InsertTimeDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
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
            EditorTextBox.TextArea.TextView.InvalidateVisual();
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
        RemoveHighlightAdorner();
        EditorTextBox.Focus();
    }
}
