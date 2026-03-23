using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastNote.App.Settings;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;

namespace FastNote.App;

public partial class MainWindow
{
    private const int ReadBufferChars = 256 * 1024;
    private const int UiAppendChunkChars = 6 * 1024 * 1024;
    private static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(650);
    private static readonly IReadOnlyList<SaveOptionItem> EncodingOptions =
    [
        new() { Key = "utf-8", Label = "UTF-8" },
        new() { Key = "utf-8-bom", Label = "UTF-8 with BOM" },
        new() { Key = "utf-16-le", Label = "Unicode" },
        new() { Key = "utf-16-be", Label = "Unicode big endian" },
        new() { Key = "ansi", Label = "ANSI" },
    ];
    private static readonly IReadOnlyList<SaveOptionItem> LineEndingOptions =
    [
        new() { Key = "crlf", Label = "Windows (CRLF)" },
        new() { Key = "lf", Label = "Unix (LF)" },
        new() { Key = "cr", Label = "Macintosh (CR)" },
    ];

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

    private bool TryRestorePreviousSession()
    {
        if (!_appSettings.RestorePreviousSession)
        {
            return false;
        }

        var session = SessionStore.Load();
        if (session is null || session.Tabs.Count == 0)
        {
            return false;
        }

        _tabs.Clear();

        foreach (var sessionTab in session.Tabs)
        {
            _tabs.Add(CreateDocumentTabFromSession(sessionTab));
        }

        _activeTabIndex = -1;
        var activeIndex = Math.Clamp(session.ActiveTabIndex, 0, _tabs.Count - 1);
        SwitchToTab(activeIndex);
        return _tabs.Count > 0;
    }

    private DocumentTab CreateDocumentTabFromSession(FastNote.App.Settings.SessionTabState state)
    {
        var tab = CreateNewDocumentTab();
        tab.Title = state.Title;
        tab.Path = state.Path;
        tab.WordWrapEnabled = state.WordWrapEnabled;
        tab.CaretIndex = state.CaretIndex;
        tab.SelectionStart = state.SelectionStart;
        tab.SelectionLength = state.SelectionLength;
        tab.EncodingKey = state.EncodingKey;
        tab.EncodingLabel = state.EncodingLabel;
        tab.LineEndingKey = state.LineEndingKey;
        tab.LineEndingLabel = state.LineEndingLabel;
        tab.IsMarkdownPreviewEnabled = state.IsMarkdownPreviewEnabled;
        tab.IsDirty = state.IsDirty;

        if (!string.IsNullOrWhiteSpace(state.DraftFileName))
        {
            var draftPath = SessionStore.GetDraftPath(state.DraftFileName);
            if (File.Exists(draftPath))
            {
                var draftText = File.ReadAllText(draftPath, ResolveEncoding(tab.EncodingKey));
                tab.EditorDocument = new TextDocument(draftText);
                tab.LoadedCharacterCount = draftText.Length;
                tab.LoadedLineCount = CountVisibleLines(draftText);
                tab.IsEditorBacked = true;
                tab.StreamedToEditorCharacterCount = draftText.Length;
                return tab;
            }
        }

        if (!string.IsNullOrWhiteSpace(tab.Path) && File.Exists(tab.Path))
        {
            tab.Title = Path.GetFileName(tab.Path);
            tab.EditorDocument = null;
            tab.IsEditorBacked = false;
            tab.Text = string.Empty;
            return tab;
        }

        tab.EditorDocument = new TextDocument();
        tab.IsEditorBacked = true;
        tab.IsDirty = false;
        return tab;
    }

    private void SaveSessionSnapshot()
    {
        CaptureActiveTabState();

        if (!_appSettings.RestorePreviousSession)
        {
            SessionStore.Save(new SessionState());
            return;
        }

        var session = new SessionState
        {
            ActiveTabIndex = Math.Max(0, _activeTabIndex),
        };
        var retainedDrafts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in _tabs)
        {
            var sessionTab = new SessionTabState
            {
                Title = tab.Title,
                Path = tab.Path,
                IsDirty = tab.IsDirty,
                WordWrapEnabled = tab.WordWrapEnabled,
                CaretIndex = tab.CaretIndex,
                SelectionStart = tab.SelectionStart,
                SelectionLength = tab.SelectionLength,
                EncodingKey = tab.EncodingKey,
                EncodingLabel = tab.EncodingLabel,
                LineEndingKey = tab.LineEndingKey,
                LineEndingLabel = tab.LineEndingLabel,
                IsMarkdownPreviewEnabled = tab.IsMarkdownPreviewEnabled,
            };

            if (tab.IsDirty || string.IsNullOrWhiteSpace(tab.Path))
            {
                var draftFileName = $"{tab.Id:N}.draft";
                var draftPath = SessionStore.GetDraftPath(draftFileName);
                Directory.CreateDirectory(SessionStore.SessionDirectoryPath);
                var content = tab.EditorDocument?.Text ?? tab.Text;
                File.WriteAllText(draftPath, content, ResolveEncoding(tab.EncodingKey));
                sessionTab.DraftFileName = draftFileName;
                retainedDrafts.Add(draftFileName);
            }

            session.Tabs.Add(sessionTab);
        }

        SessionStore.Save(session);
        SessionStore.ClearMissingDrafts(retainedDrafts);
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
            tab.EncodingKey = ToEncodingKey(reader.CurrentEncoding);
            tab.EncodingLabel = ToEncodingLabel(tab.EncodingKey);

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
        tab.LineEndingKey = DetectTrackedLineEndingKey(sawCrLf, sawCr, sawLf);
        tab.LineEndingLabel = ToLineEndingLabel(tab.LineEndingKey);
        AddRecentFile(path);

        if (GetActiveTab()?.Id == tab.Id)
        {
            tab.EditorDocument!.UndoStack.SizeLimit = 1_024;
            tab.LoadedCharacterCount = tab.EditorDocument.TextLength;
            tab.LoadedLineCount = tab.EditorDocument.LineCount;
            tab.StreamedToEditorCharacterCount = tab.EditorDocument.TextLength;
            UpdateLoadingUi();
            UpdateStatusBar();
            RefreshMarkdownPreview(tab);
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
        SaveSessionSnapshot();
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
        var shouldKeepMarkdownPreview = tab.IsMarkdownPreviewEnabled;
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
        tab.EncodingKey = "utf-8";
        tab.EncodingLabel = "UTF-8";
        tab.LineEndingKey = "crlf";
        tab.LineEndingLabel = "Windows (CRLF)";
        tab.IsTextCacheReady = true;
        tab.IsHydratingText = false;
        tab.AutoActivateEditorWhenReady = false;
        tab.IsMarkdownPreviewEnabled = shouldKeepMarkdownPreview && SupportsMarkdownPreview(tab);
        tab.MarkdownPreviewCacheKey = null;
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

        var fileName = string.IsNullOrWhiteSpace(tab.Path) ? tab.Title : Path.GetFileName(tab.Path);
        var dialog = new UnsavedChangesDialog(fileName) { Owner = this };
        _ = dialog.ShowDialog();

        if (dialog.Choice == UnsavedChangesChoice.Cancel)
        {
            return false;
        }

        if (dialog.Choice == UnsavedChangesChoice.Save)
        {
            return await SaveDocumentAsync(tab, saveAs: false);
        }

        return true;
    }

    private async Task<bool> SaveDocumentAsync(DocumentTab? tab, bool saveAs, bool chooseOptions = false)
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
                Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = SupportsMarkdownPreview(tab) ? "md" : "txt",
                FileName = string.IsNullOrWhiteSpace(path) ? (SupportsMarkdownPreview(tab) ? "Untitled.md" : "Untitled.txt") : Path.GetFileName(path),
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        if (chooseOptions)
        {
            var dialog = new SaveOptionsWindow(EncodingOptions, LineEndingOptions, tab.EncodingKey, tab.LineEndingKey) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            tab.EncodingKey = dialog.SelectedEncodingKey;
            tab.EncodingLabel = ToEncodingLabel(tab.EncodingKey);
            tab.LineEndingKey = dialog.SelectedLineEndingKey;
            tab.LineEndingLabel = ToLineEndingLabel(tab.LineEndingKey);
        }

        var snapshot = tab.EditorDocument?.CreateSnapshot();
        var sourceText = snapshot?.Text ?? tab.Text;
        var normalizedText = NormalizeLineEndings(sourceText, tab.LineEndingKey);
        var encoding = ResolveEncoding(tab.EncodingKey);
        tab.LoadedCharacterCount = normalizedText.Length;
        tab.LoadedLineCount = CountVisibleLines(normalizedText);

        tab.IsLoading = true;
        tab.LoadingLabel = "Saving…";
        RenderTabs();
        if (GetActiveTab()?.Id == tab.Id)
        {
            UpdateLoadingUi();
        }

        try
        {
            await File.WriteAllTextAsync(path!, normalizedText, encoding);
        }
        finally
        {
            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
        }

        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.IsMarkdownPreviewEnabled = tab.IsMarkdownPreviewEnabled && SupportsMarkdownPreview(tab);
        tab.MarkdownPreviewCacheKey = null;
        tab.IsDirty = false;
        tab.EncodingLabel = ToEncodingLabel(tab.EncodingKey);
        tab.LineEndingLabel = ToLineEndingLabel(tab.LineEndingKey);
        AddRecentFile(path!);
        RenderTabs();
        UpdateTitle();
        RefreshActiveTabUi();
        SaveSessionSnapshot();
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
        UpdateMarkdownUi(tab);
        RenderTabs();
    }

    private void UpdateEditorSurface(DocumentTab? tab)
    {
        var showMarkdownPreview = IsMarkdownPreviewActive(tab);
        MarkdownPreviewBrowser.Visibility = showMarkdownPreview ? Visibility.Visible : Visibility.Collapsed;
        EditorTextBox.Visibility = showMarkdownPreview ? Visibility.Collapsed : Visibility.Visible;

        if (showMarkdownPreview)
        {
            RefreshMarkdownPreview(tab);
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
        tab.MarkdownPreviewCacheKey = null;
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
        UpdateMarkdownUi(tab);
        UpdateTitle();
    }

    private static Encoding ResolveEncoding(string encodingKey)
    {
        return encodingKey switch
        {
            "utf-8-bom" => new UTF8Encoding(true),
            "utf-16-le" => Encoding.Unicode,
            "utf-16-be" => Encoding.BigEndianUnicode,
            "ansi" => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage),
            _ => new UTF8Encoding(false),
        };
    }

    private static string ToEncodingKey(Encoding encoding)
    {
        if (encoding is UTF8Encoding utf8Encoding)
        {
            return utf8Encoding.GetPreamble().Length > 0 ? "utf-8-bom" : "utf-8";
        }

        if (encoding.CodePage == Encoding.Unicode.CodePage)
        {
            return "utf-16-le";
        }

        if (encoding.CodePage == Encoding.BigEndianUnicode.CodePage)
        {
            return "utf-16-be";
        }

        return "ansi";
    }

    private static string ToEncodingLabel(string encodingKey)
    {
        return EncodingOptions.FirstOrDefault(option => string.Equals(option.Key, encodingKey, StringComparison.OrdinalIgnoreCase))?.Label ?? "UTF-8";
    }

    private static string DetectTrackedLineEndingKey(bool sawCrLf, bool sawCr, bool sawLf)
    {
        if (sawCrLf)
            return "crlf";
        if (sawCr)
            return "cr";
        if (sawLf)
            return "lf";
        return "crlf";
    }

    private static string ToLineEndingLabel(string lineEndingKey)
    {
        return LineEndingOptions.FirstOrDefault(option => string.Equals(option.Key, lineEndingKey, StringComparison.OrdinalIgnoreCase))?.Label ?? "Windows (CRLF)";
    }

    private static string NormalizeLineEndings(string value, string lineEndingKey)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var target = lineEndingKey switch
        {
            "lf" => "\n",
            "cr" => "\r",
            _ => "\r\n",
        };

        return normalized.Replace("\n", target, StringComparison.Ordinal);
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
            EncodingKey = "utf-8",
            Text = string.Empty,
            EditorDocument = new TextDocument(),
            IsEditorBacked = true,
            LastActivatedUtc = DateTime.UtcNow,
            WordWrapEnabled = _appSettings.DefaultWordWrap,
            LineEndingKey = "crlf",
            LineEndingLabel = "Windows (CRLF)",
        };
    }

    private void CreateNewTabAndActivate()
    {
        CaptureActiveTabState();
        _tabs.Add(CreateNewDocumentTab());
        SwitchToTab(_tabs.Count - 1);
        SaveSessionSnapshot();
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
        var isWindows11Appearance = _appearanceMode == AppAppearanceMode.Windows11;
        var maxTabWidth = isWindows11Appearance ? 240d : 200d;
        var minTabWidth = isWindows11Appearance ? 112d : 96d;
        var tabGap = isWindows11Appearance ? 4d : 2d;
        var newTabButtonWidth = 34d;
        var availableWidth = TabStripScrollViewer.ActualWidth;
        if (availableWidth <= 0)
        {
            availableWidth = Width > 0 ? Width - 250 : 700;
        }

        var computedWidth = _tabs.Count == 0
            ? maxTabWidth
            : Math.Floor((availableWidth - newTabButtonWidth - Math.Max(0, _tabs.Count) * tabGap) / _tabs.Count);
        var targetTabWidth = Math.Clamp(computedWidth, minTabWidth, maxTabWidth);

        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var isActive = i == _activeTabIndex;

            var border = new Border
            {
                Height = isWindows11Appearance ? 30 : 34,
                Width = targetTabWidth,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = isWindows11Appearance ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(6, 6, 0, 0),
                Padding = isWindows11Appearance ? new Thickness(14, 0, 8, 0) : new Thickness(12, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = isWindows11Appearance ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 2, 0),
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

        var newTabButton = new Button
        {
            Width = isWindows11Appearance ? 30 : 32,
            Height = isWindows11Appearance ? 30 : 32,
            Margin = isWindows11Appearance ? new Thickness(2, 0, 0, 0) : new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
            ToolTip = "New tab (Ctrl+T)",
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var newTabSurface = new Border
        {
            Width = isWindows11Appearance ? 28 : 30,
            Height = isWindows11Appearance ? 28 : 30,
            CornerRadius = isWindows11Appearance ? new CornerRadius(8) : new CornerRadius(10),
            Background = (Brush)FindResource("TabInactiveBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = isWindows11Appearance ? new Thickness(0) : new Thickness(1),
            Child = new TextBlock
            {
                Text = "\uE710",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11,
                Foreground = (Brush)FindResource("MenuForegroundBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        newTabButton.Content = newTabSurface;
        newTabButton.MouseEnter += (_, _) => newTabSurface.Background = (Brush)FindResource("TabHoverBrush");
        newTabButton.MouseLeave += (_, _) => newTabSurface.Background = (Brush)FindResource("TabInactiveBrush");
        newTabButton.Click += NewTabButton_OnClick;
        TabStripPanel.Children.Add(newTabButton);

        Dispatcher.BeginInvoke(UpdateTabStripNavigationState, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateTabStripNavigationState()
    {
        var canScroll = TabStripScrollViewer.ExtentWidth - TabStripScrollViewer.ViewportWidth > 1;
        var canScrollLeft = canScroll && TabStripScrollViewer.HorizontalOffset > 1;
        var canScrollRight = canScroll && TabStripScrollViewer.HorizontalOffset < TabStripScrollViewer.ScrollableWidth - 1;

        ApplyTabScrollButtonState(TabScrollLeftButton, TabScrollLeftSurface, canScrollLeft);
        ApplyTabScrollButtonState(TabScrollRightButton, TabScrollRightSurface, canScrollRight);
    }

    private void ApplyTabScrollButtonState(Button button, Border surface, bool isEnabled)
    {
        button.IsEnabled = isEnabled;
        button.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        button.Opacity = 1;
        surface.Background = (Brush)FindResource("TabInactiveBrush");
    }

    private void TabScrollLeftButton_OnClick(object sender, RoutedEventArgs e)
    {
        ScrollTabsBy(-Math.Max(160, TabStripScrollViewer.ViewportWidth * 0.6));
    }

    private void TabScrollRightButton_OnClick(object sender, RoutedEventArgs e)
    {
        ScrollTabsBy(Math.Max(160, TabStripScrollViewer.ViewportWidth * 0.6));
    }

    private void ScrollTabsBy(double delta)
    {
        var nextOffset = Math.Clamp(TabStripScrollViewer.HorizontalOffset + delta, 0, TabStripScrollViewer.ScrollableWidth);
        TabStripScrollViewer.ScrollToHorizontalOffset(nextOffset);
        UpdateTabStripNavigationState();
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
        SaveSessionSnapshot();
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
        SaveSessionSnapshot();
    }
}
