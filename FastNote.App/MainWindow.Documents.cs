using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastNote.App.Settings;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;

namespace FastNote.App;

public partial class MainWindow
{
    private const int ReadBufferChars = 128 * 1024;
    private const int LoadAppendChunkChars = 512 * 1024;
    private const int SaveWriteChunkChars = 128 * 1024;
    private static readonly TimeSpan LoadUiFlushInterval = TimeSpan.FromMilliseconds(120);

    private sealed record LoadedTabContent(string Text, string EncodingKey, string EncodingLabel, string LineEndingKey, string LineEndingLabel, long CharacterCount);

    private static readonly IReadOnlyList<SaveOptionItem> EncodingOptions =
    [
        new() { Key = "utf-8", Label = "UTF-8" },
        new() { Key = "utf-8-bom", Label = "UTF-8 with BOM" },
        new() { Key = "utf-16-le", Label = "Unicode" },
        new() { Key = "utf-16-be", Label = "Unicode big endian" },
        new() { Key = "ansi", Label = "ANSI" },
    ];

    private static readonly IReadOnlyList<SaveOptionItem> LineEndingOptions = [new() { Key = "crlf", Label = "Windows (CRLF)" }, new() { Key = "lf", Label = "Unix (LF)" }, new() { Key = "cr", Label = "Macintosh (CR)" }];

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

    public Task OpenStartupFilesAsync(IEnumerable<string> paths)
    {
        var pendingPaths = GetExistingPaths(paths);
        if (pendingPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (CanReusePlaceholderTabForFileOpen())
        {
            var activeTab = GetActiveTab();
            if (activeTab is not null)
            {
                var firstPath = pendingPaths[0];
                pendingPaths.RemoveAt(0);
                _ = StartLoadingIntoTabAsync(activeTab, firstPath);
            }
        }

        if (pendingPaths.Count > 0)
        {
            return OpenFilesInNewTabsAsync(pendingPaths);
        }

        return Task.CompletedTask;
    }

    public Task OpenFilesFromSecondaryLaunchAsync(IEnumerable<string> paths)
    {
        ActivateFromExternalOpen();
        return OpenFilesInNewTabsAsync(paths);
    }

    public Task OpenFilesInNewTabsAsync(IEnumerable<string> paths)
    {
        var validPaths = GetExistingPaths(paths);
        if (validPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        CaptureActiveTabState();
        DocumentTab? lastTab = null;

        foreach (var path in validPaths)
        {
            var tab = CreateNewDocumentTab();
            _tabs.Add(tab);
            lastTab = tab;
        }

        if (lastTab is not null)
        {
            _activeTabIndex = _tabs.Count - 1;
            PresentTab(lastTab, forceTextRefresh: true);
        }

        QueueSessionSnapshot();

        foreach (var (path, index) in validPaths.Select((path, index) => (path, index)))
        {
            var tab = _tabs[_tabs.Count - validPaths.Count + index];
            _ = StartLoadingIntoTabAsync(tab, path);
        }

        return Task.CompletedTask;
    }

    private static List<string> GetExistingPaths(IEnumerable<string> paths)
    {
        return paths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool CanReusePlaceholderTabForFileOpen()
    {
        var tab = GetActiveTab();
        if (tab is null || _tabs.Count != 1 || tab.IsDirty || tab.IsLoading || !string.IsNullOrWhiteSpace(tab.Path))
        {
            return false;
        }

        var text = tab.EditorDocument?.Text ?? tab.Text;
        return string.IsNullOrEmpty(text) && string.Equals(tab.Title, "Untitled.txt", StringComparison.OrdinalIgnoreCase);
    }

    private void ActivateFromExternalOpen()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
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

        if (_tabs.Count == 0)
        {
            return false;
        }

        _activeTabIndex = -1;
        SwitchToTab(Math.Clamp(session.ActiveTabIndex, 0, _tabs.Count - 1));
        return true;
    }

    private DocumentTab CreateDocumentTabFromSession(SessionTabState state)
    {
        var tab = CreateNewDocumentTab();
        tab.Title = state.Title;
        tab.Path = state.Path;
        tab.Kind = DetectDocumentKind(state.Path, state.Title);
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
                var document = CreateTextDocument(draftText, 1_024);
                tab.EditorDocument = document;
                tab.LoadedCharacterCount = draftText.Length;
                tab.LoadedLineCount = CountVisibleLines(draftText);
                tab.IsEditorBacked = true;
                return tab;
            }
        }

        if (!string.IsNullOrWhiteSpace(tab.Path) && File.Exists(tab.Path))
        {
            tab.Title = Path.GetFileName(tab.Path);
            tab.Kind = DetectDocumentKind(tab.Path, tab.Title);
            tab.EditorDocument = null;
            tab.Text = string.Empty;
            tab.IsEditorBacked = false;
            tab.IsDirty = false;
            return tab;
        }

        tab.EditorDocument = CreateTextDocument(string.Empty, 1_024);
        tab.IsEditorBacked = true;
        tab.IsDirty = false;
        return tab;
    }

    private void UpdateSpellCheckState(DocumentTab? tab)
    {
        var isEnabled = _spellCheckColorizer.IsAvailable && tab is not null && !tab.IsLoading && !IsMarkdownPreviewActive(tab) && (tab.IsMarkdownDocument || ResolveSyntaxLanguage(tab) == SyntaxLanguage.None);

        if (_spellCheckColorizer.IsEnabled == isEnabled)
        {
            return;
        }

        _spellCheckColorizer.IsEnabled = isEnabled;
        _spellCheckColorizer.ClearCache();
        _editor.Redraw();
    }

    private void SaveSessionSnapshot()
    {
        _sessionSnapshotPending = false;
        CaptureActiveTabState();

        if (!_appSettings.RestorePreviousSession)
        {
            SessionStore.Save(new SessionState());
            return;
        }

        var session = new SessionState { ActiveTabIndex = Math.Max(0, _activeTabIndex) };
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
                File.WriteAllText(draftPath, tab.EditorDocument?.Text ?? tab.Text, ResolveEncoding(tab.EncodingKey));
                sessionTab.DraftFileName = draftFileName;
                retainedDrafts.Add(draftFileName);
            }

            session.Tabs.Add(sessionTab);
        }

        SessionStore.Save(session);
        SessionStore.ClearMissingDrafts(retainedDrafts);
    }

    private void QueueSessionSnapshot()
    {
        _sessionSnapshotPending = true;
    }

    private void FlushPendingSessionSnapshot()
    {
        if (!_sessionSnapshotPending)
        {
            return;
        }

        SaveSessionSnapshot();
    }

    private async Task StartLoadingIntoTabAsync(DocumentTab tab, string path)
    {
        CancelLoad(tab);
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
            if (await TryLoadFileFromCacheAsync(tab, path, loadVersion, tokenSource.Token))
            {
                return;
            }

            await StreamFileIntoEditorAsync(tab, path, loadVersion, tokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (tab.LoadVersion != loadVersion)
            {
                return;
            }

            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
            if (GetActiveTab()?.Id == tab.Id)
            {
                UpdateLoadingUi();
                UpdateStatusBar();
            }

            RenderTabs();
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
            if (_loadTokens.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, tokenSource))
            {
                _loadTokens.Remove(tab.Id);
            }

            tokenSource.Dispose();
        }
    }

    private async Task StreamFileIntoEditorAsync(DocumentTab tab, string path, int loadVersion, CancellationToken cancellationToken)
    {
        var document = StreamingLoadDocument(tab);
        var fileLength = new FileInfo(path).Length;
        var cacheBuilder = new StringBuilder(fileLength > 0 ? (int)Math.Min(fileLength, LoadAppendChunkChars) : ReadBufferChars);

        var sawCrLf = false;
        var sawCr = false;
        var sawLf = false;
        var previousEndedWithCr = false;
        var encodingKey = tab.EncodingKey;
        var encodingLabel = tab.EncodingLabel;
        string? cachedText = null;

        await Task.Run(
            async () =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 1024, options: FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 1024, leaveOpen: false);

                var readBuffer = new char[ReadBufferChars];
                var pendingChunk = new StringBuilder(LoadAppendChunkChars);
                var loadedCharacters = 0L;
                var flushStopwatch = Stopwatch.StartNew();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = reader.Read(readBuffer, 0, readBuffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    pendingChunk.Append(readBuffer, 0, read);
                    cacheBuilder?.Append(readBuffer, 0, read);
                    TrackLineEndings(readBuffer, read, ref sawCrLf, ref sawCr, ref sawLf, ref previousEndedWithCr);
                    loadedCharacters += read;
                    encodingKey = ToEncodingKey(reader.CurrentEncoding);
                    encodingLabel = ToEncodingLabel(encodingKey);

                    var shouldFlush = pendingChunk.Length >= LoadAppendChunkChars || flushStopwatch.Elapsed >= LoadUiFlushInterval;
                    if (!shouldFlush)
                    {
                        continue;
                    }

                    var chunk = pendingChunk.ToString();
                    pendingChunk.Clear();
                    flushStopwatch.Restart();

                    if (!await AppendLoadedChunkAsync(tab, loadVersion, document, chunk, loadedCharacters, encodingKey, encodingLabel, fileLength, cancellationToken))
                    {
                        return;
                    }
                }

                if (pendingChunk.Length > 0)
                {
                    var chunk = pendingChunk.ToString();
                    pendingChunk.Clear();

                    if (!await AppendLoadedChunkAsync(tab, loadVersion, document, chunk, loadedCharacters, encodingKey, encodingLabel, fileLength, cancellationToken))
                    {
                        return;
                    }
                }

                cachedText = cacheBuilder?.ToString();
            },
            cancellationToken
        );

        cancellationToken.ThrowIfCancellationRequested();
        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        var lineEndingKey = DetectTrackedLineEndingKey(sawCrLf, sawCr, sawLf);
        CompleteStreamingLoad(tab, loadVersion, document, encodingKey, encodingLabel, lineEndingKey);
        AddRecentFile(path);
        RenderTabs();
        TrimInactiveTabMemory();
        QueueSessionSnapshot();

        if (cachedText is not null)
        {
            QueuePersistentFileCacheWrite(path, cachedText, tab.EncodingKey, tab.LineEndingKey, tab.LoadedLineCount);
        }
    }

    private async Task<bool> TryLoadFileFromCacheAsync(DocumentTab tab, string path, int loadVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_appSettings.EnableFileOpenCache)
        {
            return false;
        }

        var loadedContent = await LoadCachedDocumentAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (loadedContent is null || tab.LoadVersion != loadVersion)
        {
            return false;
        }

        ApplyLoadedDocumentToTab(tab, loadedContent);
        AddRecentFile(path);
        RenderTabs();
        TrimInactiveTabMemory();
        QueueSessionSnapshot();
        return true;
    }

    private async Task<LoadedTabContent?> LoadCachedDocumentAsync(string path, CancellationToken cancellationToken)
    {
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!FileOpenCache.TryLoad(path, out var snapshot) || snapshot is null)
                {
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new LoadedTabContent(snapshot.Text, snapshot.EncodingKey, ToEncodingLabel(snapshot.EncodingKey), snapshot.LineEndingKey, ToLineEndingLabel(snapshot.LineEndingKey), snapshot.CharacterCount);
            },
            cancellationToken
        );
    }

    private TextDocument StreamingLoadDocument(DocumentTab tab)
    {
        var document = CreateTextDocument(string.Empty, 0);
        tab.EditorDocument = document;
        tab.Text = string.Empty;
        tab.IsEditorBacked = true;

        if (GetActiveTab()?.Id == tab.Id)
        {
            SetEditorDocumentFast(document);
            _editor.IsReadOnly = true;
            UpdateEditorSurface(tab);
            ConfigureWordWrap();
            UpdateLoadingUi();
            UpdateStatusBar();
        }

        return document;
    }

    private async Task<bool> AppendLoadedChunkAsync(DocumentTab tab, int loadVersion, TextDocument document, string chunk, long loadedCharacters, string encodingKey, string encodingLabel, long fileLength, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return true;
        }

        return await Dispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tab.LoadVersion != loadVersion)
                {
                    return false;
                }

                AppendChunkToDocument(tab, document, chunk);
                tab.LoadedCharacterCount = loadedCharacters;
                tab.LoadedLineCount = document.LineCount;
                tab.EncodingKey = encodingKey;
                tab.EncodingLabel = encodingLabel;
                tab.LoadingLabel = BuildLoadingLabel(loadedCharacters, fileLength);

                if (GetActiveTab()?.Id == tab.Id)
                {
                    UpdateLoadingUi();
                    UpdateStatusBar();
                }

                return true;
            },
            System.Windows.Threading.DispatcherPriority.Background,
            cancellationToken
        );
    }

    private void AppendChunkToDocument(DocumentTab tab, TextDocument document, string chunk)
    {
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

            if (GetActiveTab()?.Id == tab.Id && !ReferenceEquals(_editor.Document, document))
            {
                _editor.SetDocument(document);
            }
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    private void CompleteStreamingLoad(DocumentTab tab, int loadVersion, TextDocument document, string encodingKey, string encodingLabel, string lineEndingKey)
    {
        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        document.UndoStack.SizeLimit = 1_024;
        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.IsEditorBacked = true;
        tab.Text = string.Empty;
        tab.EditorDocument = document;
        tab.IsDirty = false;
        tab.EncodingKey = encodingKey;
        tab.EncodingLabel = encodingLabel;
        tab.LineEndingKey = lineEndingKey;
        tab.LineEndingLabel = ToLineEndingLabel(lineEndingKey);
        tab.LoadedCharacterCount = document.TextLength;
        tab.LoadedLineCount = document.LineCount;

        if (GetActiveTab()?.Id == tab.Id)
        {
            FinalizeActiveStreamingLoad(tab, document);
        }
    }

    private static string BuildLoadingLabel(long loadedCharacters, long fileLength)
    {
        var percent = fileLength <= 0 ? 100 : Math.Min(100, (int)Math.Round(loadedCharacters * 100d / fileLength));
        return $"Loading… {percent}%";
    }

    private void FinalizeActiveStreamingLoad(DocumentTab tab, TextDocument document)
    {
        if (!ReferenceEquals(_editor.Document, document))
        {
            SetEditorDocumentFast(document);
        }

        _editor.IsReadOnly = false;
        ApplySyntaxHighlighting(tab);
        UpdateEditorSurface(tab);
        ConfigureWordWrap();
        UpdateTitle();
        UpdateLoadingUi();
        UpdateStatusBar();
        UpdateMarkdownUi(tab);
        RenderTabs();

        Dispatcher.BeginInvoke(
            () =>
            {
                if (GetActiveTab()?.Id != tab.Id || !ReferenceEquals(_editor.Document, document))
                {
                    return;
                }

                _editor.Focus();
            },
            System.Windows.Threading.DispatcherPriority.Background
        );
    }

    private void ApplyLoadedDocumentToTab(DocumentTab tab, LoadedTabContent loadedContent)
    {
        var document = CreateTextDocument(loadedContent.Text, 1_024);
        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.IsEditorBacked = true;
        tab.Text = string.Empty;
        tab.EditorDocument = document;
        tab.IsDirty = false;
        tab.EncodingKey = loadedContent.EncodingKey;
        tab.EncodingLabel = loadedContent.EncodingLabel;
        tab.LineEndingKey = loadedContent.LineEndingKey;
        tab.LineEndingLabel = loadedContent.LineEndingLabel;
        tab.LoadedCharacterCount = loadedContent.CharacterCount;
        tab.LoadedLineCount = document.LineCount;

        if (GetActiveTab()?.Id == tab.Id)
        {
            PresentTab(tab, forceTextRefresh: true);
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

    private void ResetTabForLoad(DocumentTab tab, string path)
    {
        var keepMarkdownPreview = tab.IsMarkdownPreviewEnabled;

        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Kind = DetectDocumentKind(path, tab.Title);
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
        tab.EncodingKey = "utf-8";
        tab.EncodingLabel = "UTF-8";
        tab.LineEndingKey = "crlf";
        tab.LineEndingLabel = "Windows (CRLF)";
        tab.IsMarkdownPreviewEnabled = keepMarkdownPreview && tab.IsMarkdownDocument;
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

        return dialog.Choice switch
        {
            UnsavedChangesChoice.Cancel => false,
            UnsavedChangesChoice.Save => await SaveDocumentAsync(tab, saveAs: false),
            _ => true,
        };
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
                DefaultExt = tab.IsMarkdownDocument ? "md" : "txt",
                FileName = string.IsNullOrWhiteSpace(path) ? GetDisplayName(tab) : Path.GetFileName(path),
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        if (chooseOptions)
        {
            var optionsDialog = new SaveOptionsWindow(EncodingOptions, LineEndingOptions, tab.EncodingKey, tab.LineEndingKey) { Owner = this };
            if (optionsDialog.ShowDialog() != true)
            {
                return false;
            }

            tab.EncodingKey = optionsDialog.SelectedEncodingKey;
            tab.EncodingLabel = ToEncodingLabel(tab.EncodingKey);
            tab.LineEndingKey = optionsDialog.SelectedLineEndingKey;
            tab.LineEndingLabel = ToLineEndingLabel(tab.LineEndingKey);
        }

        var snapshot = tab.EditorDocument?.CreateSnapshot();
        var sourceText = snapshot?.Text ?? tab.Text;
        var encoding = ResolveEncoding(tab.EncodingKey);

        tab.LoadedCharacterCount = sourceText.Length;
        tab.LoadedLineCount = CountVisibleLines(sourceText);
        tab.IsLoading = true;
        tab.LoadingLabel = "Saving…";
        RenderTabs();

        if (GetActiveTab()?.Id == tab.Id)
        {
            UpdateLoadingUi();
            UpdateStatusBar();
        }

        try
        {
            await WriteDocumentTextAsync(path!, sourceText, encoding, tab.LineEndingKey);
        }
        finally
        {
            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
        }

        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Kind = DetectDocumentKind(path, tab.Title);
        tab.IsMarkdownPreviewEnabled = tab.IsMarkdownPreviewEnabled && tab.IsMarkdownDocument;
        tab.MarkdownPreviewCacheKey = null;
        tab.IsDirty = false;
        tab.EncodingLabel = ToEncodingLabel(tab.EncodingKey);
        tab.LineEndingLabel = ToLineEndingLabel(tab.LineEndingKey);

        AddRecentFile(path!);
        RenderTabs();
        UpdateTitle();
        RefreshActiveTabUi();
        SaveSessionSnapshot();
        QueuePersistentFileCacheWrite(path!, sourceText, tab.EncodingKey, tab.LineEndingKey, tab.LoadedLineCount);
        return true;
    }

    private void QueuePersistentFileCacheWrite(string path, string text, string encodingKey, string lineEndingKey, long lineCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !_appSettings.EnableFileOpenCache)
        {
            return;
        }

        _ = Task.Run(() => FileOpenCache.StoreAsync(path, text, encodingKey, lineEndingKey, lineCount));
    }

    private static async Task WriteDocumentTextAsync(string path, string text, Encoding encoding, string lineEndingKey)
    {
        var targetLineEnding = lineEndingKey switch
        {
            "lf" => "\n",
            "cr" => "\r",
            _ => "\r\n",
        };

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 128 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, encoding, bufferSize: 128 * 1024, leaveOpen: false);

        if (string.IsNullOrEmpty(text))
        {
            await writer.FlushAsync();
            return;
        }

        var pending = new StringBuilder(Math.Min(Math.Max(text.Length / 8, SaveWriteChunkChars), SaveWriteChunkChars * 4));
        var segmentStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n'))
            {
                continue;
            }

            if (i > segmentStart)
            {
                pending.Append(text, segmentStart, i - segmentStart);
            }

            pending.Append(targetLineEnding);

            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            segmentStart = i + 1;

            if (pending.Length < SaveWriteChunkChars)
            {
                continue;
            }

            await writer.WriteAsync(pending.ToString());
            pending.Clear();
        }

        if (segmentStart < text.Length)
        {
            pending.Append(text, segmentStart, text.Length - segmentStart);
        }

        if (pending.Length > 0)
        {
            await writer.WriteAsync(pending.ToString());
        }

        await writer.FlushAsync();
    }

    private void CaptureActiveTabState()
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        tab.EditorDocument = _editor.Document;
        tab.LoadedCharacterCount = tab.EditorDocument?.TextLength ?? 0;
        tab.LoadedLineCount = tab.EditorDocument?.LineCount ?? 1;
        tab.CaretIndex = _editor.CaretOffset;
        tab.SelectionStart = _editor.SelectionStart;
        tab.SelectionLength = _editor.SelectionLength;
        tab.LastActivatedUtc = DateTime.UtcNow;
    }

    private void PresentTab(DocumentTab tab, bool forceTextRefresh)
    {
        tab.LastActivatedUtc = DateTime.UtcNow;
        UpdateEditorSurface(tab);
        ApplySyntaxHighlighting(tab);

        var document = tab.EditorDocument ?? CreateDocumentFromTabText(tab);
        SetEditorDocumentFast(document);
        _editor.IsReadOnly = tab.IsLoading;

        var safeLength = _editor.Document?.TextLength ?? 0;
        var caretIndex = Math.Min(tab.CaretIndex, safeLength);
        var selectionStart = Math.Min(tab.SelectionStart, safeLength);
        var selectionLength = Math.Min(tab.SelectionLength, safeLength - selectionStart);

        _editor.Select(0, 0);
        _editor.CaretOffset = Math.Min(caretIndex, Math.Min(safeLength, 1_024));
        Dispatcher.BeginInvoke(
            () =>
            {
                if (GetActiveTab()?.Id != tab.Id || !ReferenceEquals(_editor.Document, document))
                {
                    return;
                }

                var delayedSafeLength = _editor.Document?.TextLength ?? 0;
                var delayedCaretIndex = Math.Min(tab.CaretIndex, delayedSafeLength);
                var delayedSelectionStart = Math.Min(tab.SelectionStart, delayedSafeLength);
                var delayedSelectionLength = Math.Min(tab.SelectionLength, delayedSafeLength - delayedSelectionStart);

                _editor.CaretOffset = delayedCaretIndex;
                _editor.Select(delayedSelectionStart, delayedSelectionLength);
                _editor.Focus();
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
        _editor.Visibility = showMarkdownPreview ? Visibility.Collapsed : Visibility.Visible;

        if (showMarkdownPreview)
        {
            RefreshMarkdownPreview(tab);
        }
    }

    private void SetEditorDocumentFast(TextDocument document)
    {
        _isInternalUpdate = true;
        try
        {
            _editor.SetDocument(document);
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    private static TextDocument CreateTextDocument(string text, int undoSizeLimit)
    {
        var document = new TextDocument(text);
        document.UndoStack.SizeLimit = undoSizeLimit;
        return document;
    }

    private TextDocument CreateDocumentFromTabText(DocumentTab tab)
    {
        var document = CreateTextDocument(tab.Text, 1_024);
        tab.EditorDocument = document;
        tab.Text = string.Empty;
        tab.MarkdownPreviewCacheKey = null;
        return document;
    }

    private void RefreshActiveTabUi()
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        _editor.IsReadOnly = tab.IsLoading;
        ApplySyntaxHighlighting(tab);
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
        {
            return "crlf";
        }

        if (sawCr)
        {
            return "cr";
        }

        if (sawLf)
        {
            return "lf";
        }

        return "crlf";
    }

    private static string ToLineEndingLabel(string lineEndingKey)
    {
        return LineEndingOptions.FirstOrDefault(option => string.Equals(option.Key, lineEndingKey, StringComparison.OrdinalIgnoreCase))?.Label ?? "Windows (CRLF)";
    }

    private void ApplySyntaxHighlighting(DocumentTab? tab)
    {
        _editor.SyntaxHighlighting = null;
        _syntaxHighlightColorizer.Language = ResolveSyntaxLanguage(tab);
        _syntaxHighlightColorizer.IsDarkTheme = _themeMode == AppThemeMode.Dark;
        _editor.Redraw();
    }

    private static SyntaxLanguage ResolveSyntaxLanguage(DocumentTab? tab)
    {
        var candidate = !string.IsNullOrWhiteSpace(tab?.Path) ? tab!.Path : tab?.Title;
        var extension = Path.GetExtension(candidate);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return SyntaxLanguage.None;
        }

        return extension.ToLowerInvariant() switch
        {
            ".cs" or ".csx" => SyntaxLanguage.CSharp,
            ".json" => SyntaxLanguage.Json,
            ".py" => SyntaxLanguage.Python,
            ".js" or ".cjs" or ".mjs" or ".ts" or ".tsx" => SyntaxLanguage.JavaScript,
            ".xml" or ".xaml" or ".resx" or ".config" or ".csproj" or ".props" or ".targets" or ".svg" => SyntaxLanguage.Xml,
            ".html" or ".htm" or ".xhtml" => SyntaxLanguage.Html,
            ".css" => SyntaxLanguage.Css,
            ".cpp" or ".cxx" or ".cc" or ".h" or ".hpp" => SyntaxLanguage.Cpp,
            _ => SyntaxLanguage.None,
        };
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
            Title = "Untitled.txt",
            EncodingLabel = "UTF-8",
            EncodingKey = "utf-8",
            Text = string.Empty,
            EditorDocument = CreateTextDocument(string.Empty, 1_024),
            IsEditorBacked = true,
            LastActivatedUtc = DateTime.UtcNow,
            WordWrapEnabled = _appSettings.DefaultWordWrap,
            LineEndingKey = "crlf",
            LineEndingLabel = "Windows (CRLF)",
            Kind = DocumentKind.PlainText,
        };
    }

    private void CreateNewTabAndActivate(bool saveSession = true)
    {
        CaptureActiveTabState();
        _tabs.Add(CreateNewDocumentTab());
        SwitchToTab(_tabs.Count - 1);

        if (saveSession)
        {
            QueueSessionSnapshot();
        }
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        if (index == _activeTabIndex)
        {
            _editor.Focus();
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
        {
            return;
        }

        var currentIndex = _activeTabIndex < 0 ? 0 : _activeTabIndex;
        var nextIndex = ((currentIndex + offset) % _tabs.Count + _tabs.Count) % _tabs.Count;
        SwitchToTab(nextIndex);
    }

    private void TrimInactiveTabMemory()
    {
        if (_tabs.Count <= TabRetentionLimit)
        {
            return;
        }

        var activeId = GetActiveTab()?.Id;
        var retained = _tabs.OrderByDescending(tab => tab.Id == activeId).ThenByDescending(tab => tab.LastActivatedUtc).Take(TabRetentionLimit).Select(tab => tab.Id).ToHashSet();

        foreach (var tab in _tabs)
        {
            if (retained.Contains(tab.Id) || tab.IsDirty || tab.IsLoading || string.IsNullOrWhiteSpace(tab.Path))
            {
                continue;
            }

            tab.EditorDocument = null;
            tab.Text = string.Empty;
            tab.IsEditorBacked = false;
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

    private void RenderTabs()
    {
        TabStripPanel.Children.Clear();

        const double maxTabWidth = 244d;
        const double minTabWidth = 132d;
        const double tabGap = 6d;
        const double newTabButtonWidth = 34d;

        var availableWidth = TabStripScrollViewer.ActualWidth;
        if (availableWidth <= 0)
        {
            availableWidth = Width > 0 ? Width - 250 : 700;
        }

        var computedWidth = _tabs.Count == 0 ? maxTabWidth : Math.Floor((availableWidth - newTabButtonWidth - Math.Max(0, _tabs.Count) * tabGap) / _tabs.Count);
        var targetTabWidth = Math.Clamp(computedWidth, minTabWidth, maxTabWidth);

        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var isActive = i == _activeTabIndex;

            var border = new Border
            {
                Height = 32,
                Width = targetTabWidth,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(14, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = tab.Id,
                AllowDrop = true,
            };
            border.ContextMenu = CreateTabContextMenu(tab.Id);
            border.MouseRightButtonUp += TabBorder_OnMouseRightButtonUp;

            if (!isActive)
            {
                border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("TabHoverBrush");
                border.MouseLeave += (_, _) => border.Background = (Brush)FindResource("TabInactiveBrush");
            }

            border.DragEnter += TabBorder_OnDragOver;
            border.DragOver += TabBorder_OnDragOver;
            border.Drop += TabBorder_OnDrop;

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
                    FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            titleButton.Click += TabButton_OnClick;
            titleButton.PreviewMouseLeftButtonDown += TabButton_OnPreviewMouseLeftButtonDown;
            titleButton.PreviewMouseMove += TabButton_OnPreviewMouseMove;
            titleButton.PreviewMouseLeftButtonUp += TabButton_OnPreviewMouseLeftButtonUp;

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
            Width = 32,
            Height = 32,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
            ToolTip = "New tab (Ctrl+T)",
            Cursor = Cursors.Hand,
        };

        var newTabSurface = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("TabInactiveBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
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
        var index = _tabs.FindIndex(tab => tab.Id == tabId);
        if (index < 0)
        {
            return;
        }

        var tab = _tabs[index];
        if (!await ConfirmDiscardChangesAsync(tab))
        {
            return;
        }

        CancelLoad(tab);

        var targetIndex = index == _activeTabIndex ? Math.Max(0, Math.Min(index, _tabs.Count - 2)) : _activeTabIndex;
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (index < _activeTabIndex)
        {
            targetIndex--;
        }

        _activeTabIndex = -1;
        SwitchToTab(Math.Max(0, Math.Min(targetIndex, _tabs.Count - 1)));
        QueueSessionSnapshot();
    }

    private async Task CloseOtherTabsAsync(Guid tabId)
    {
        var retainedTab = _tabs.FirstOrDefault(tab => tab.Id == tabId);
        if (retainedTab is null)
        {
            return;
        }

        foreach (var tab in _tabs.Where(tab => tab.Id != tabId).ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                return;
            }
        }

        foreach (var tab in _tabs.Where(tab => tab.Id != tabId).ToArray())
        {
            CancelLoad(tab);
            _tabs.Remove(tab);
        }

        _activeTabIndex = -1;
        SwitchToTab(Math.Max(0, _tabs.FindIndex(tab => tab.Id == tabId)));
        QueueSessionSnapshot();
    }

    private async Task CloseTabsToRightAsync(Guid tabId)
    {
        var index = _tabs.FindIndex(tab => tab.Id == tabId);
        if (index < 0 || index >= _tabs.Count - 1)
        {
            return;
        }

        foreach (var tab in _tabs.Skip(index + 1).ToArray())
        {
            if (!await ConfirmDiscardChangesAsync(tab))
            {
                return;
            }
        }

        foreach (var tab in _tabs.Skip(index + 1).ToArray())
        {
            CancelLoad(tab);
            _tabs.Remove(tab);
        }

        _activeTabIndex = -1;
        SwitchToTab(Math.Max(0, Math.Min(index, _tabs.Count - 1)));
        QueueSessionSnapshot();
    }

    private void MoveTab(Guid draggedTabId, Guid targetTabId)
    {
        if (draggedTabId == targetTabId)
        {
            return;
        }

        var sourceIndex = _tabs.FindIndex(tab => tab.Id == draggedTabId);
        var targetIndex = _tabs.FindIndex(tab => tab.Id == targetTabId);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        var tab = _tabs[sourceIndex];
        _tabs.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        _tabs.Insert(targetIndex, tab);
        _activeTabIndex = _tabs.FindIndex(item => item.Id == draggedTabId);
        RenderTabs();
        QueueSessionSnapshot();
    }

    private void TabButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: int index })
        {
            return;
        }

        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        _pendingTabDragId = _tabs[index].Id;
        _tabDragStartPoint = e.GetPosition(TabStripScrollViewer);
    }

    private void TabButton_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingTabDragId is null || _draggingTabId is not null)
        {
            return;
        }

        var currentPoint = e.GetPosition(TabStripScrollViewer);
        var delta = currentPoint - _tabDragStartPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _draggingTabId = _pendingTabDragId;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(Guid), _draggingTabId.Value), DragDropEffects.Move);
        }
        finally
        {
            _pendingTabDragId = null;
            _draggingTabId = null;
        }
    }

    private void TabButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pendingTabDragId = null;
    }

    private void TabBorder_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Guid)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabBorder_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Guid)) || sender is not Border { Tag: Guid targetTabId })
        {
            return;
        }

        if (e.Data.GetData(typeof(Guid)) is Guid draggedTabId)
        {
            MoveTab(draggedTabId, targetTabId);
            SwitchToTab(_tabs.FindIndex(tab => tab.Id == draggedTabId));
        }

        e.Handled = true;
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
        {
            return;
        }

        if (tab is null)
        {
            return;
        }

        CancelLoad(tab);
        var replacement = CreateNewDocumentTab();
        var index = _tabs.IndexOf(tab);
        _tabs[index] = replacement;
        _activeTabIndex = index;
        PresentTab(replacement, forceTextRefresh: true);
        QueueSessionSnapshot();
    }
}
