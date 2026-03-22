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

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N:
                    e.Handled = true;
                    CreateNewTabAndActivate();
                    return;
                case Key.O:
                    e.Handled = true;
                    await OpenWithDialogAsync();
                    return;
                case Key.S when Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift):
                    e.Handled = true;
                    await SaveDocumentAsync(GetActiveTab(), saveAs: true);
                    return;
                case Key.S:
                    e.Handled = true;
                    await SaveDocumentAsync(GetActiveTab(), saveAs: false);
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
                case Key.OemComma:
                    e.Handled = true;
                    SettingsMenuItem_OnClick(this, new RoutedEventArgs());
                    return;
                case Key.A:
                    break;
            }
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.N)
            {
                e.Handled = true;
                NewWindowMenuItem_OnClick(this, new RoutedEventArgs());
                return;
            }
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
        foreach (var tab in _tabs.ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                e.Cancel = true;
                return;
            }
        }

        foreach (var tab in _tabs)
        {
            CancelLoad(tab);
            ReleaseVirtualDocument(tab);
        }

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
        tab.Title = string.IsNullOrWhiteSpace(tab.Path) ? "Untitled" : Path.GetFileName(tab.Path);
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

    private static string DetectLineEnding(string value)
    {
        if (value.Contains("\r\n", StringComparison.Ordinal))
        {
            return "Windows (CRLF)";
        }

        if (value.Contains('\r'))
        {
            return "Macintosh (CR)";
        }

        if (value.Contains('\n'))
        {
            return "Unix (LF)";
        }

        return "Windows (CRLF)";
    }

    private static string DetectLineEnding(ITextSource textSource)
    {
        using var reader = textSource.CreateReader();
        var buffer = new char[8_192];
        var previousWasCr = false;
        var sawCr = false;
        var sawLf = false;

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    if (previousWasCr)
                    {
                        return "Windows (CRLF)";
                    }

                    sawLf = true;
                }
                else if (ch == '\r')
                {
                    sawCr = true;
                }

                previousWasCr = ch == '\r';
            }
        }

        if (sawCr)
            return "Macintosh (CR)";
        if (sawLf)
            return "Unix (LF)";
        return "Windows (CRLF)";
    }

    private static string ToEncodingLabel(Encoding encoding)
    {
        return encoding.EncodingName.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ? "UTF-8" : encoding.EncodingName;
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source)
    {
        while (source is not null)
        {
            switch (source)
            {
                case Button:
                case TextBox:
                case ScrollViewer:
                    return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
