using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastNote.App;

public partial class MainWindow
{
    private const int StreamChunkChars = 32 * 1024;
    private const int ReadBufferChars = 256 * 1024;

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
            await LoadTabAsync(tab, path, loadVersion, tokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (tab.LoadVersion == loadVersion)
            {
                tab.IsLoading = false;
                tab.LoadingLabel = string.Empty;
                if (GetActiveTab()?.Id == tab.Id)
                {
                    _isInternalUpdate = true;
                    EditorTextBox.Text = string.Empty;
                    _isInternalUpdate = false;
                    EditorTextBox.IsReadOnly = false;
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
                _isInternalUpdate = true;
                EditorTextBox.Text = string.Empty;
                _isInternalUpdate = false;
                EditorTextBox.IsReadOnly = false;
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

    private async Task LoadTabAsync(DocumentTab tab, string path, int loadVersion, CancellationToken cancellationToken)
    {
        var tabId = tab.Id;

        var (fullContent, encoding, sawCrLf, sawCr, sawLf) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024);

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024 * 1024,
                leaveOpen: true);

            var fileLength = stream.Length;
            var capacity = (int)Math.Min(Math.Max(4096L, fileLength), 512L * 1024 * 1024);
            var sb = new StringBuilder(capacity);
            var charBuf = new char[ReadBufferChars];
            var sawCrLfL = false;
            var sawCrL = false;
            var sawLfL = false;
            var prevCr = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.Read(charBuf, 0, charBuf.Length);
                if (read <= 0)
                {
                    break;
                }

                sb.Append(charBuf, 0, read);
                TrackLineEndings(charBuf, read, ref sawCrLfL, ref sawCrL, ref sawLfL, ref prevCr);
            }

            return (sb.ToString(), reader.CurrentEncoding, sawCrLfL, sawCrL, sawLfL);
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        tab.IsEditorBacked = false;
        tab.EncodingLabel = ToEncodingLabel(encoding);
        tab.LineEndingLabel = DetectTrackedLineEnding(sawCrLf, sawCr, sawLf);
        tab.LoadedCharacterCount = fullContent.Length;
        tab.LoadedLineCount = 1;
        tab.Text = string.Empty;

        var totalChars = fullContent.Length;
        var committed = 0;
        var isActiveTab = GetActiveTab()?.Id == tabId;

        if (isActiveTab)
        {
            _isInternalUpdate = true;
            EditorTextBox.Text = string.Empty;
            _isInternalUpdate = false;
            EditorTextBox.IsReadOnly = true;
        }

        while (committed < totalChars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (tab.LoadVersion != loadVersion)
            {
                return;
            }

            var chunkSize = Math.Min(StreamChunkChars, totalChars - committed);
            var chunk = fullContent.Substring(committed, chunkSize);
            committed += chunkSize;

            var percent = committed * 100 / totalChars;
            tab.LoadingLabel = $"Loading\u2026 {percent}%";

            isActiveTab = GetActiveTab()?.Id == tabId;
            if (isActiveTab)
            {
                _isInternalUpdate = true;
                EditorTextBox.AppendText(chunk);
                _isInternalUpdate = false;
                tab.StreamedToEditorCharacterCount = committed;
                tab.LoadedLineCount = EditorTextBox.LineCount;
                UpdateLoadingUi();
            }

            await Task.Yield();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (tab.LoadVersion != loadVersion)
        {
            return;
        }

        tab.Text = fullContent;
        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.IsEditorBacked = true;
        tab.LoadedCharacterCount = fullContent.Length;
        tab.LoadedLineCount = CountVisibleLines(fullContent);

        isActiveTab = GetActiveTab()?.Id == tabId;
        if (isActiveTab)
        {
            EditorTextBox.IsReadOnly = false;
            EditorTextBox.CaretIndex = 0;
            tab.StreamedToEditorCharacterCount = fullContent.Length;
            ConfigureWordWrap();
            UpdateTitle();
            UpdateLoadingUi();
            UpdateStatusBar();
        }

        RenderTabs();
        TrimInactiveTabMemory();
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
        if (sawCrLf) return "Windows (CRLF)";
        if (sawCr) return "Macintosh (CR)";
        if (sawLf) return "Unix (LF)";
        return "Windows (CRLF)";
    }

    private void ResetTabForLoad(DocumentTab tab, string path)
    {
        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Text = string.Empty;
        tab.IsDirty = false;
        tab.IsLoading = true;
        tab.LoadingLabel = "Loading\u2026";
        tab.LoadedCharacterCount = 0;
        tab.LoadedLineCount = 0;
        tab.CaretIndex = 0;
        tab.SelectionStart = 0;
        tab.SelectionLength = 0;
        tab.LastActivatedUtc = DateTime.UtcNow;
        tab.IsEditorBacked = false;
        tab.StreamedToEditorCharacterCount = 0;
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

        var result = MessageBox.Show(
            this,
            $"Do you want to save changes to {tab.DisplayTitle}?",
            "Notepad",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return await SaveDocumentAsync(tab, saveAs: false);
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
                FileName = string.IsNullOrWhiteSpace(path) ? "Untitled.txt" : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        var enc = tab.LineEndingLabel.Contains("UTF-8 BOM") ? new UTF8Encoding(true) : new UTF8Encoding(false);
        await File.WriteAllTextAsync(path!, tab.Text, enc);
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
        if (tab is null) return;

        if (!tab.IsLoading)
        {
            tab.Text = EditorTextBox.Text;
            tab.LoadedCharacterCount = tab.Text.Length;
            tab.LoadedLineCount = CountVisibleLines(tab.Text);
            tab.CaretIndex = EditorTextBox.CaretIndex;
            tab.SelectionStart = EditorTextBox.SelectionStart;
            tab.SelectionLength = EditorTextBox.SelectionLength;
        }

        tab.LastActivatedUtc = DateTime.UtcNow;
    }

    private void PresentTab(DocumentTab tab, bool forceTextRefresh)
    {
        tab.LastActivatedUtc = DateTime.UtcNow;

        if (tab.IsLoading)
        {
            _isInternalUpdate = true;
            EditorTextBox.Text = string.Empty;
            _isInternalUpdate = false;
            EditorTextBox.IsReadOnly = true;
            tab.StreamedToEditorCharacterCount = 0;
        }
        else
        {
            var displayText = tab.Text;
            if (forceTextRefresh || EditorTextBox.Text != displayText)
            {
                _isInternalUpdate = true;
                EditorTextBox.Text = displayText;
                _isInternalUpdate = false;
            }

            EditorTextBox.IsReadOnly = false;
            tab.StreamedToEditorCharacterCount = displayText.Length;

            var safeLen = EditorTextBox.Text.Length;
            var caretIndex = Math.Min(tab.CaretIndex, safeLen);
            var selStart = Math.Min(tab.SelectionStart, safeLen);
            var selLen = Math.Min(tab.SelectionLength, safeLen - selStart);
            EditorTextBox.CaretIndex = caretIndex;
            EditorTextBox.Select(selStart, selLen);
        }

        ConfigureWordWrap();
        UpdateTitle();
        UpdateLoadingUi();
        UpdateStatusBar();
        RenderTabs();
    }

    private void RefreshActiveTabUi()
    {
        var tab = GetActiveTab();
        if (tab is null) return;

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
            IsEditorBacked = true,
            LastActivatedUtc = DateTime.UtcNow
        };
    }

    private void CreateNewTabAndActivate()
    {
        CaptureActiveTabState();
        _tabs.Add(CreateNewDocumentTab());
        SwitchToTab(_tabs.Count - 1);
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

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

        EditorTextBox.Focus();
        TrimInactiveTabMemory();
    }

    private void SwitchTabByOffset(int offset)
    {
        if (_tabs.Count <= 1 || offset == 0) return;

        var currentIndex = _activeTabIndex < 0 ? 0 : _activeTabIndex;
        var nextIndex = ((currentIndex + offset) % _tabs.Count + _tabs.Count) % _tabs.Count;
        SwitchToTab(nextIndex);
    }

    private void TrimInactiveTabMemory()
    {
        if (_tabs.Count <= TabRetentionLimit) return;

        var activeId = GetActiveTab()?.Id;
        var retained = _tabs
            .OrderByDescending(t => t.Id == activeId)
            .ThenByDescending(t => t.LastActivatedUtc)
            .Take(TabRetentionLimit)
            .Select(t => t.Id)
            .ToHashSet();

        foreach (var t in _tabs)
        {
            if (retained.Contains(t.Id) || t.IsDirty || t.IsLoading || string.IsNullOrWhiteSpace(t.Path))
            {
                continue;
            }

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
                Margin = new Thickness(0, 0, 2, 0)
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
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
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
        if (index < 0) return;

        var tab = _tabs[index];
        if (!await ConfirmDiscardChangesAsync(tab)) return;

        CancelLoad(tab);

        var targetIndex = index == _activeTabIndex
            ? Math.Max(0, Math.Min(index, _tabs.Count - 2))
            : _activeTabIndex;
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            CreateNewTabAndActivate();
            return;
        }

        if (index < _activeTabIndex) targetIndex--;

        _activeTabIndex = -1;
        SwitchToTab(Math.Max(0, Math.Min(targetIndex, _tabs.Count - 1)));
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt;*.log;*.md;*.json;*.xml;*.csv;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.html;*.css)|*.txt;*.log;*.md;*.json;*.xml;*.csv;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.html;*.css|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async Task NewDocumentAsync()
    {
        var tab = GetActiveTab();
        if (!await ConfirmDiscardChangesAsync(tab)) return;
        if (tab is null) return;

        CancelLoad(tab);
        var replacement = CreateNewDocumentTab();
        var index = _tabs.IndexOf(tab);
        _tabs[index] = replacement;
        _activeTabIndex = index;
        PresentTab(replacement, forceTextRefresh: true);
    }
}