using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace FastNote.App;

public partial class MainWindow
{
    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Tab)
        {
            e.Handled = true;
            SwitchTabByOffset(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.S)
            {
                e.Handled = true;
                await SaveDocumentAsync(GetActiveTab(), saveAs: true);
                return;
            }

            if (e.Key == Key.N)
            {
                e.Handled = true;
                NewWindowMenuItem_OnClick(this, new RoutedEventArgs());
                return;
            }

            if (e.Key == Key.W)
            {
                e.Handled = true;
                CloseWindowMenuItem_OnClick(this, new RoutedEventArgs());
                return;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.T:
                    e.Handled = true;
                    CreateNewTabAndActivate();
                    return;
                case Key.N:
                    e.Handled = true;
                    CreateNewTabAndActivate();
                    return;
                case Key.O:
                    e.Handled = true;
                    await OpenWithDialogAsync();
                    return;
                case Key.S:
                    e.Handled = true;
                    await SaveDocumentAsync(GetActiveTab(), saveAs: false);
                    return;
                case Key.Y:
                    e.Handled = true;
                    EditorTextBox.Redo();
                    return;
                case Key.F:
                    e.Handled = true;
                    OpenFindPanel(showReplace: false);
                    return;
                case Key.H:
                    e.Handled = true;
                    OpenFindPanel(showReplace: true);
                    return;
                case Key.G:
                    e.Handled = true;
                    GoToMenuItem_OnClick(this, new RoutedEventArgs());
                    return;
                case Key.W:
                    e.Handled = true;
                    var activeTab = GetActiveTab();
                    if (activeTab is not null)
                    {
                        await CloseTabAsync(activeTab.Id);
                    }

                    return;
                case Key.Add:
                case Key.OemPlus:
                    e.Handled = true;
                    ZoomBy(2);
                    return;
                case Key.Subtract:
                case Key.OemMinus:
                    e.Handled = true;
                    ZoomBy(-2);
                    return;
                case Key.D0:
                case Key.NumPad0:
                    e.Handled = true;
                    EditorTextBox.FontSize = DefaultEditorFontSize;
                    UpdateZoomStatus();
                    return;
                case Key.P:
                    e.Handled = true;
                    PrintMenuItem_OnClick(this, new RoutedEventArgs());
                    return;
                case Key.E:
                    e.Handled = true;
                    SearchWithBingMenuItem_OnClick(this, new RoutedEventArgs());
                    return;
                case Key.OemComma:
                    e.Handled = true;
                    SettingsMenuItem_OnClick(this, new RoutedEventArgs());
                    return;
                case Key.A:
                    break;
            }
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key == Key.S)
        {
            e.Handled = true;
            SaveAllMenuItem_OnClick(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPreviousInternal();
            }
            else
            {
                FindNextInternal();
            }

            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            InsertTimeDateButton_OnClick(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Escape && FindPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseFindPanelButton_OnClick(this, new RoutedEventArgs());
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        CaptureActiveTabState();

        if (!_skipCloseConfirmation)
        {
            foreach (var tab in _tabs.ToArray())
            {
                if (!await ConfirmDiscardChangesAsync(tab))
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
        else
        {
            _skipCloseConfirmation = false;
        }

        foreach (var tab in _tabs)
        {
            CancelLoad(tab);
            ReleaseVirtualDocument(tab);
        }

        SaveSessionSnapshot();
        _sessionSaveTimer.Stop();
        _statusRefreshTimer.Stop();
        base.OnClosing(e);
    }

    private void EditorTextBox_OnTextChanged(object? sender, EventArgs e)
    {
        if (_isInternalUpdate)
        {
            return;
        }

        var tab = GetActiveTab();
        if (tab is null || tab.IsLoading)
        {
            return;
        }

        var wasDirty = tab.IsDirty;
        tab.IsDirty = true;
        tab.Title = string.IsNullOrWhiteSpace(tab.Path) ? GetDisplayName(tab) : Path.GetFileName(tab.Path);
        tab.LoadedCharacterCount = EditorTextBox.Document?.TextLength ?? 0;
        tab.LoadedLineCount = EditorTextBox.Document?.LineCount ?? 1;
        tab.MarkdownPreviewCacheKey = null;

        if (!wasDirty)
        {
            RenderTabs();
        }

        UpdateTitle();
        UpdateStatusBar();
        RefreshMarkdownPreview(tab);

        if (FindPanel.Visibility == Visibility.Visible)
        {
            UpdateMatchCountLabel();
        }
    }

    private void EditorTextBox_OnSelectionChanged(object? sender, EventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is null || tab.IsLoading)
        {
            return;
        }

        tab.CaretIndex = EditorTextBox.CaretOffset;
        tab.SelectionStart = EditorTextBox.SelectionStart;
        tab.SelectionLength = EditorTextBox.SelectionLength;
        UpdateStatusBar();
    }

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (IsBlockedDropSource(e.OriginalSource as DependencyObject))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (TryGetDroppedFiles(e, out var files))
        {
            await OpenFilesInNewTabsAsync(files);
            e.Handled = true;
        }
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        if (IsBlockedDropSource(e.OriginalSource as DependencyObject))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = TryGetDroppedFiles(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool TryGetDroppedFiles(DragEventArgs e, out string[] files)
    {
        files = [];
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] droppedFiles || droppedFiles.Length == 0)
        {
            return false;
        }

        files = droppedFiles.Where(File.Exists).ToArray();
        return files.Length > 0;
    }

    private static long CountLineBreaks(string value)
    {
        long count = 0;
        foreach (var ch in value)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static long CountVisibleLines(string value)
    {
        return string.IsNullOrEmpty(value) ? 1 : CountLineBreaks(value) + 1;
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source)
    {
        while (source is not null)
        {
            switch (source)
            {
                case Button:
                case TextBox:
                    return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private bool IsBlockedDropSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, TopChrome))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
