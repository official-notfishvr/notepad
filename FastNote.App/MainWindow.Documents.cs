using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;

namespace FastNote.App;

public partial class MainWindow
{
    private const int ReadBufferChars = 256 * 1024;
    private const int UiAppendChunkChars = 6 * 1024 * 1024;
    private static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(650);

    public async Task OpenFileAsync(string path)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        if (!await ConfirmDiscardChangesAsync(tab))
        {
            return;
        }

        await StartLoadingIntoTabAsync(tab, path);
    }

    private async Task StartLoadingIntoTabAsync(DocumentTab tab, string path)
    {
        CancelLoad(tab);
        ReleaseVirtualDocument(tab);
        ResetTabForLoad(tab, path);

        if (GetActiveTab()?.Id == tab.Id)
        {
            PresentTab(tab, forceTextRefresh: true);
        }

        var tokenSource = new CancellationTokenSource();
        _loadTokens[tab.Id] = tokenSource;
        var loadVersion = ++tab.LoadVersion;

        try
        {
            await StreamFileIntoEditorAsync(tab, path, loadVersion, tokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (tab.LoadVersion == loadVersion)
            {
                tab.IsLoading = false;
                tab.LoadingLabel = string.Empty;
                if (GetActiveTab()?.Id == tab.Id)
                {
                    UpdateLoadingUi();
                }

                RenderTabs();
            }
        }
        catch (Exception ex)
        {
            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;

            if (GetActiveTab()?.Id == tab.Id)
            {
                MessageBox.Show(this, ex.Message, "Notepad", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshActiveTabUi();
            }

            RenderTabs();
        }
        finally
        {
            if (_loadTokens.TryGetValue(tab.Id, out var current) && current == tokenSource)
            {
                _loadTokens.Remove(tab.Id);
            }

            tokenSource.Dispose();
        }
    }

    private async Task StreamFileIntoEditorAsync(DocumentTab tab, string path, int loadVersion, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 1024, leaveOpen: true);

        var fileLength = stream.Length;
        var charBuffer = new char[ReadBufferChars];
        var active = GetActiveTab()?.Id == tab.Id;
        var pendingUiChunk = new StringBuilder(UiAppendChunkChars);
        var sawCrLf = false;
        var sawCr = false;
        var sawLf = false;
        var previousEndedWithCr = false;
        var flushStopwatch = Stopwatch.StartNew();
        var document = new TextDocument();
        document.UndoStack.SizeLimit = 0;

        tab.IsLoading = true;
        tab.IsEditorBacked = true;
        tab.IsViewportBacked = false;
        tab.Text = string.Empty;
        tab.EditorDocument = document;
        tab.IsTextCacheReady = true;
        tab.IsHydratingText = false;
        tab.AutoActivateEditorWhenReady = false;
        tab.LoadedCharacterCount = 0;
        tab.LoadedLineCount = 1;
        tab.StreamedToEditorCharacterCount = 0;

        if (active)
        {
            SetEditorDocumentFast(document);
            EditorTextBox.IsReadOnly = false;

            UpdateEditorSurface(tab);
            ConfigureWordWrap();
            UpdateLoadingUi();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(charBuffer.AsMemory(0, charBuffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (tab.LoadVersion != loadVersion)
            {
                return;
            }

            pendingUiChunk.Append(charBuffer, 0, read);
            TrackLineEndings(charBuffer, read, ref sawCrLf, ref sawCr, ref sawLf, ref previousEndedWithCr);
            tab.LoadedCharacterCount += read;
            tab.EncodingLabel = ToEncodingLabel(reader.CurrentEncoding);

            var shouldFlush = pendingUiChunk.Length >= UiAppendChunkChars || flushStopwatch.Elapsed >= UiFlushInterval;

            if (!shouldFlush)
            {
                continue;
            }

            await FlushEditorChunkAsync(tab, loadVersion, pendingUiChunk, fileLength, cancellationToken);
            flushStopwatch.Restart();
        }

        await FlushEditorChunkAsync(tab, loadVersion, pendingUiChunk, fileLength, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.LineEndingLabel = DetectTrackedLineEnding(sawCrLf, sawCr, sawLf);

        if (GetActiveTab()?.Id == tab.Id)
        {
            tab.EditorDocument!.UndoStack.SizeLimit = 1_024;
            tab.LoadedCharacterCount = tab.EditorDocument.TextLength;
            tab.LoadedLineCount = tab.EditorDocument.LineCount;
            tab.StreamedToEditorCharacterCount = tab.EditorDocument.TextLength;
            UpdateLoadingUi();
            UpdateStatusBar();
        }
        else
        {
            tab.EditorDocument!.UndoStack.SizeLimit = 1_024;
            tab.LoadedCharacterCount = tab.EditorDocument.TextLength;
            tab.LoadedLineCount = tab.EditorDocument.LineCount;
            tab.StreamedToEditorCharacterCount = tab.EditorDocument.TextLength;
        }

        RenderTabs();
        TrimInactiveTabMemory();
    }

    private async Task FlushEditorChunkAsync(DocumentTab tab, int loadVersion, StringBuilder pendingUiChunk, long fileLength, CancellationToken cancellationToken)
    {
        if (pendingUiChunk.Length == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var chunk = pendingUiChunk.ToString();
        pendingUiChunk.Clear();

        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        var active = GetActiveTab()?.Id == tab.Id;
        var percent = fileLength <= 0 ? 100 : Math.Min(100, (int)Math.Round(tab.LoadedCharacterCount * 100d / fileLength));
        tab.LoadingLabel = $"Loading… {percent}%";

        if (active)
        {
            AppendEditorChunkPreservingSelection(tab, chunk);
            tab.StreamedToEditorCharacterCount = tab.EditorDocument?.TextLength ?? 0;
            tab.LoadedLineCount = tab.EditorDocument?.LineCount ?? 1;
            UpdateLoadingUi();
        }
        else
        {
            AppendChunkToDocument(tab, chunk);
            tab.StreamedToEditorCharacterCount = tab.EditorDocument?.TextLength ?? 0;
            tab.LoadedLineCount = tab.EditorDocument?.LineCount ?? 1;
        }

        await Task.Yield();
    }

    private void AppendEditorChunkPreservingSelection(DocumentTab tab, string chunk)
    {
        var selectionStart = EditorTextBox.SelectionStart;
        var selectionLength = EditorTextBox.SelectionLength;
        var caretIndex = EditorTextBox.CaretOffset;
        var restoreSelection = EditorTextBox.IsKeyboardFocusWithin;

        AppendChunkToDocument(tab, chunk);

        if (!restoreSelection)
        {
            return;
        }

        var safeLength = EditorTextBox.Document?.TextLength ?? 0;
        var safeSelectionStart = Math.Min(selectionStart, safeLength);
        var safeSelectionLength = Math.Min(selectionLength, safeLength - safeSelectionStart);
        EditorTextBox.Select(safeSelectionStart, safeSelectionLength);
        EditorTextBox.CaretOffset = Math.Min(caretIndex, safeLength);
    }

    private void AppendChunkToDocument(DocumentTab tab, string chunk)
    {
        var document = tab.EditorDocument ?? new TextDocument();
        tab.EditorDocument = document;

        _isInternalUpdate = true;
        try
        {
            document.BeginUpdate();
            try
            {
                document.Insert(document.TextLength, chunk);
            }
            finally
            {
                document.EndUpdate();
            }

            if (GetActiveTab()?.Id == tab.Id && !ReferenceEquals(EditorTextBox.Document, document))
            {
                EditorTextBox.Document = document;
            }
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    private static void TrackLineEndings(char[] buffer, int count, ref bool sawCrLf, ref bool sawCr, ref bool sawLf, ref bool previousEndedWithCr)
    {
        for (var i = 0; i < count; i++)
        {
            var ch = buffer[i];
            if (ch == '\n')
            {
                sawLf = true;
                if ((i > 0 && buffer[i - 1] == '\r') || (i == 0 && previousEndedWithCr))
                {
                    sawCrLf = true;
                }
            }
            else if (ch == '\r')
            {
                sawCr = true;
            }
        }

        previousEndedWithCr = count > 0 && buffer[count - 1] == '\r';
    }

    private static string DetectTrackedLineEnding(bool sawCrLf, bool sawCr, bool sawLf)
    {
        if (sawCrLf)
            return "Windows (CRLF)";
        if (sawCr)
            return "Macintosh (CR)";
        if (sawLf)
            return "Unix (LF)";
        return "Windows (CRLF)";
    }

    private void ResetTabForLoad(DocumentTab tab, string path)
    {
        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Text = string.Empty;
        tab.EditorDocument = null;
        tab.IsDirty = false;
        tab.IsLoading = true;
        tab.LoadingLabel = "Loading…";
        tab.LoadedCharacterCount = 0;
        tab.LoadedLineCount = 1;
        tab.CaretIndex = 0;
        tab.SelectionStart = 0;
        tab.SelectionLength = 0;
        tab.LastActivatedUtc = DateTime.UtcNow;
        tab.IsEditorBacked = true;
        tab.IsViewportBacked = false;
        tab.StreamedToEditorCharacterCount = 0;
        tab.EncodingLabel = "UTF-8";
        tab.LineEndingLabel = "Windows (CRLF)";
        tab.IsTextCacheReady = true;
        tab.IsHydratingText = false;
        tab.AutoActivateEditorWhenReady = false;
        CloseMenus();
        RenderTabs();
        UpdateTitle();
    }

    private async Task<bool> ConfirmDiscardChangesAsync(DocumentTab? tab)
    {
        if (tab is null || !tab.IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(this, $"Do you want to save changes to {tab.DisplayTitle}?", "Notepad", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return false;
        if (result == MessageBoxResult.Yes)
            return await SaveDocumentAsync(tab, saveAs: false);
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
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = string.IsNullOrWhiteSpace(path) ? "Untitled.txt" : Path.GetFileName(path),
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        var enc = tab.LineEndingLabel.Contains("UTF-8 BOM") ? new UTF8Encoding(true) : new UTF8Encoding(false);
        var snapshot = tab.EditorDocument?.CreateSnapshot();
        var fallbackText = snapshot is null ? tab.Text : null;
        tab.LoadedCharacterCount = snapshot?.TextLength ?? fallbackText?.Length ?? 0;
        tab.LoadedLineCount = tab.EditorDocument?.LineCount ?? CountVisibleLines(fallbackText ?? string.Empty);

        tab.IsLoading = true;
        tab.LoadingLabel = "Saving…";
        RenderTabs();
        if (GetActiveTab()?.Id == tab.Id)
        {
            UpdateLoadingUi();
        }

        try
        {
            if (snapshot is not null)
            {
                tab.LineEndingLabel = await Task.Run(() => DetectLineEnding(snapshot));
                await WriteTextSourceToFileAsync(path!, snapshot, enc);
            }
            else
            {
                var content = fallbackText ?? string.Empty;
                tab.LineEndingLabel = await Task.Run(() => DetectLineEnding(content));
                await File.WriteAllTextAsync(path!, content, enc);
            }
        }
        finally
        {
            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
        }

        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.IsDirty = false;
        tab.EncodingLabel = "UTF-8";
        RenderTabs();
        UpdateTitle();
        RefreshActiveTabUi();
        return true;
    }

    private void CaptureActiveTabState()
    {
        var tab = GetActiveTab();
        if (tab is null)
            return;

        if (!tab.IsViewportBacked)
        {
            tab.EditorDocument = EditorTextBox.Document;
            tab.LoadedCharacterCount = tab.EditorDocument?.TextLength ?? 0;
            tab.LoadedLineCount = tab.EditorDocument?.LineCount ?? 1;
            tab.CaretIndex = EditorTextBox.CaretOffset;
            tab.SelectionStart = EditorTextBox.SelectionStart;
            tab.SelectionLength = EditorTextBox.SelectionLength;
        }

        tab.LastActivatedUtc = DateTime.UtcNow;
    }

    private void PresentTab(DocumentTab tab, bool forceTextRefresh)
    {
        tab.LastActivatedUtc = DateTime.UtcNow;
        UpdateEditorSurface(tab);

        var document = tab.EditorDocument ?? CreateDocumentFromTabText(tab);
        SetEditorDocumentFast(document);

        EditorTextBox.IsReadOnly = false;
        tab.StreamedToEditorCharacterCount = document.TextLength;

        var safeLen = EditorTextBox.Document?.TextLength ?? 0;
        var caretIndex = Math.Min(tab.CaretIndex, safeLen);
        var selStart = Math.Min(tab.SelectionStart, safeLen);
        var selLen = Math.Min(tab.SelectionLength, safeLen - selStart);
        EditorTextBox.Select(0, 0);
        EditorTextBox.CaretOffset = Math.Min(caretIndex, Math.Min(safeLen, 1_024));
        Dispatcher.BeginInvoke(
            () =>
            {
                if (GetActiveTab()?.Id != tab.Id || !ReferenceEquals(EditorTextBox.Document, document))
                {
                    return;
                }

                var delayedSafeLen = EditorTextBox.Document?.TextLength ?? 0;
                var delayedCaretIndex = Math.Min(tab.CaretIndex, delayedSafeLen);
                var delayedSelStart = Math.Min(tab.SelectionStart, delayedSafeLen);
                var delayedSelLen = Math.Min(tab.SelectionLength, delayedSafeLen - delayedSelStart);
                EditorTextBox.CaretOffset = delayedCaretIndex;
                EditorTextBox.Select(delayedSelStart, delayedSelLen);
                EditorTextBox.Focus();
            },
            System.Windows.Threading.DispatcherPriority.Background
        );

        ConfigureWordWrap();
        UpdateTitle();
        UpdateLoadingUi();
        UpdateStatusBar();
        RenderTabs();
    }

    private void UpdateEditorSurface(DocumentTab? tab)
    {
        DocumentViewportControl.Visibility = Visibility.Collapsed;
        EditorTextBox.Visibility = Visibility.Visible;
        if (DocumentViewportControl.Document is not null)
        {
            DocumentViewportControl.Document = null;
        }
    }

    private void SetEditorTextFast(string value)
    {
        _isInternalUpdate = true;
        var document = new TextDocument(value);
        document.UndoStack.SizeLimit = 0;
        try
        {
            EditorTextBox.Document = document;
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    private void SetEditorDocumentFast(TextDocument document)
    {
        _isInternalUpdate = true;
        try
        {
            EditorTextBox.Document = document;
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    private TextDocument CreateDocumentFromTabText(DocumentTab tab)
    {
        var document = new TextDocument(tab.Text);
        document.UndoStack.SizeLimit = 1_024;
        tab.EditorDocument = document;
        tab.Text = string.Empty;
        return document;
    }

    private void RefreshActiveTabUi()
    {
        var tab = GetActiveTab();
        if (tab is null)
            return;

        UpdateEditorSurface(tab);
        UpdateLoadingUi();
        UpdateStatusBar();
        UpdateTitle();
    }

    private DocumentTab? GetActiveTab()
    {
        return _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
    }

    private DocumentTab CreateNewDocumentTab()
    {
        return new DocumentTab
        {
            Id = Guid.NewGuid(),
            Title = "Untitled",
            EncodingLabel = "UTF-8",
            Text = string.Empty,
            EditorDocument = new TextDocument(),
            IsEditorBacked = true,
            LastActivatedUtc = DateTime.UtcNow,
        };
    }

    private static Task WriteTextSourceToFileAsync(string path, ITextSource snapshot, Encoding encoding)
    {
        return Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, encoding, 1024 * 1024);
            snapshot.WriteTextTo(writer);
        });
    }

    private void CreateNewTabAndActivate()
    {
        CaptureActiveTabState();
        _tabs.Add(CreateNewDocumentTab());
        SwitchToTab(_tabs.Count - 1);
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return;

        if (index == _activeTabIndex)
        {
            EditorTextBox.Focus();
            return;
        }

        CaptureActiveTabState();
        _activeTabIndex = index;
        var tab = _tabs[index];

        if (!tab.IsLoading && !tab.IsEditorBacked && !tab.IsDirty && !string.IsNullOrWhiteSpace(tab.Path))
        {
            _ = StartLoadingIntoTabAsync(tab, tab.Path!);
        }
        else
        {
            PresentTab(tab, forceTextRefresh: true);
        }

        TrimInactiveTabMemory();
    }

    private void SwitchTabByOffset(int offset)
    {
        if (_tabs.Count <= 1 || offset == 0)
            return;

        var currentIndex = _activeTabIndex < 0 ? 0 : _activeTabIndex;
        var nextIndex = ((currentIndex + offset) % _tabs.Count + _tabs.Count) % _tabs.Count;
        SwitchToTab(nextIndex);
    }

    private void TrimInactiveTabMemory()
    {
        if (_tabs.Count <= TabRetentionLimit)
            return;

        var activeId = GetActiveTab()?.Id;
        var retained = _tabs.OrderByDescending(t => t.Id == activeId).ThenByDescending(t => t.LastActivatedUtc).Take(TabRetentionLimit).Select(t => t.Id).ToHashSet();

        foreach (var t in _tabs)
        {
            if (retained.Contains(t.Id) || t.IsDirty || t.IsLoading || string.IsNullOrWhiteSpace(t.Path))
            {
                continue;
            }

            t.EditorDocument = null;
            t.Text = string.Empty;
            t.IsEditorBacked = false;
        }
    }

    private void CancelLoad(DocumentTab tab)
    {
        if (_loadTokens.TryGetValue(tab.Id, out var tokenSource))
        {
            tokenSource.Cancel();
            _loadTokens.Remove(tab.Id);
        }
    }

    private void ReleaseVirtualDocument(DocumentTab tab)
    {
        tab.VirtualDocument?.Dispose();
        tab.VirtualDocument = null;
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
                Height = 34,
                MinWidth = 120,
                MaxWidth = 200,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(12, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 0),
            };

            if (!isActive)
            {
                border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("TabHoverBrush");
                border.MouseLeave += (_, _) => border.Background = (Brush)FindResource("TabInactiveBrush");
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleButton = new Button
            {
                Style = (Style)FindResource("TabButtonStyle"),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = i,
                Content = new TextBlock
                {
                    Text = tab.DisplayTitle,
                    Foreground = (Brush)FindResource(isActive ? "TabForegroundBrush" : "TabInactiveForegroundBrush"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            titleButton.Click += TabButton_OnClick;

            var closeButton = new Button
            {
                Style = (Style)FindResource("TabCloseButtonStyle"),
                Tag = tab.Id,
                Content = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("EditorMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
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
            return;

        var tab = _tabs[index];
        if (!await ConfirmDiscardChangesAsync(tab))
            return;

        CancelLoad(tab);
        ReleaseVirtualDocument(tab);

        var targetIndex = index == _activeTabIndex ? Math.Max(0, Math.Min(index, _tabs.Count - 2)) : _activeTabIndex;
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (index < _activeTabIndex)
            targetIndex--;

        _activeTabIndex = -1;
        SwitchToTab(Math.Max(0, Math.Min(targetIndex, _tabs.Count - 1)));
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Text files (*.txt;*.log;*.md;*.json;*.xml;*.csv;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.html;*.css)|*.txt;*.log;*.md;*.json;*.xml;*.csv;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.html;*.css|All files (*.*)|*.*", CheckFileExists = true };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async Task NewDocumentAsync()
    {
        var tab = GetActiveTab();
        if (!await ConfirmDiscardChangesAsync(tab))
            return;
        if (tab is null)
            return;

        CancelLoad(tab);
        ReleaseVirtualDocument(tab);
        var replacement = CreateNewDocumentTab();
        var index = _tabs.IndexOf(tab);
        _tabs[index] = replacement;
        _activeTabIndex = index;
        PresentTab(replacement, forceTextRefresh: true);
    }
}
