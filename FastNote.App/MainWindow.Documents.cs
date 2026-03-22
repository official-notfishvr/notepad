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
            if (ShouldUseLargeFilePreview(path))
            {
                await LoadLargePreviewTabAsync(tab, path, loadVersion, tokenSource.Token);
            }
            else
            {
                await LoadTabAsync(tab, path, loadVersion, tokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (tab.LoadVersion == loadVersion)
            {
                tab.LoadBuffer = null;
            }
        }
        catch (Exception ex)
        {
            tab.LoadBuffer = null;
            if (GetActiveTab()?.Id == tab.Id)
            {
                MessageBox.Show(this, ex.Message, "Notepad", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
            tab.ReadOnlyReason = "Load failed";
            if (GetActiveTab()?.Id == tab.Id)
            {
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
        var fileInfo = new FileInfo(path);
        var isLargeFile = fileInfo.Exists && fileInfo.Length >= LargeFileThresholdBytes;
        tab.Mode = isLargeFile ? DocumentMode.LargePreview : DocumentMode.Editable;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);

        var fullBuilder = new StringBuilder((int)Math.Min(fileInfo.Length, 1024 * 1024));
        var buffer = new char[64 * 1024];
        var totalLines = 0L;
        var totalCharacters = 0L;
        var lastUiRefreshUtc = DateTime.MinValue;
        tab.LoadBuffer = fullBuilder;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var chunk = new string(buffer, 0, read);
            totalCharacters += chunk.Length;
            totalLines += CountLineBreaks(chunk);

            fullBuilder.Append(chunk);

            tab.LoadedCharacterCount = totalCharacters;
            tab.LoadedLineCount = totalLines;
            tab.EncodingLabel = ToEncodingLabel(reader.CurrentEncoding);
            tab.LoadingLabel = $"Loading… {stream.Position * 100 / Math.Max(1, stream.Length):N0}%";

            if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
            {
                await Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (GetActiveTab()?.Id != tab.Id || tab.LoadVersion != loadVersion)
                        {
                            return;
                        }

                        if (tab.StreamedToEditorCharacterCount < fullBuilder.Length)
                        {
                            var appendStart = tab.StreamedToEditorCharacterCount;
                            var appendLength = fullBuilder.Length - appendStart;
                            var appendChunk = fullBuilder.ToString(appendStart, appendLength);
                            _isInternalUpdate = true;
                            EditorTextBox.AppendText(appendChunk);
                            _isInternalUpdate = false;
                            tab.StreamedToEditorCharacterCount = fullBuilder.Length;
                            tab.PreviewText = EditorTextBox.Text;
                        }
                    },
                    DispatcherPriority.Background,
                    cancellationToken);

                var now = DateTime.UtcNow;
                if ((now - lastUiRefreshUtc).TotalMilliseconds >= 140)
                {
                    lastUiRefreshUtc = now;
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
                            {
                                UpdateLoadingUi();
                                UpdateStatusBar();
                            }
                        },
                        DispatcherPriority.Background,
                        cancellationToken);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.EncodingLabel = ToEncodingLabel(reader.CurrentEncoding);
        tab.LineEndingLabel = DetectLineEnding(fullBuilder.ToString());

        tab.Text = fullBuilder.ToString();
        tab.PreviewText = tab.Text;
        tab.LoadBuffer = null;
        tab.IsEditorBacked = true;
        tab.IsReadOnly = false;
        tab.ReadOnlyReason = string.Empty;
        tab.Mode = DocumentMode.Editable;

        if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    EditorTextBox.IsReadOnly = false;
                    tab.StreamedToEditorCharacterCount = EditorTextBox.Text.Length;
                    ConfigureWordWrap();
                    UpdateTitle();
                    UpdateLoadingUi();
                    UpdateStatusBar();
                },
                DispatcherPriority.Background,
                cancellationToken);
        }

        await Dispatcher.InvokeAsync(RenderTabs, DispatcherPriority.Background, cancellationToken);
        TrimInactiveTabMemory();
    }

    private void ResetTabForLoad(DocumentTab tab, string path)
    {
        DisposePreviewDocument(tab);
        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Text = string.Empty;
        tab.PreviewText = string.Empty;
        tab.IsDirty = false;
        tab.IsLoading = true;
        tab.LoadingLabel = "Loading…";
        tab.IsReadOnly = true;
        tab.ReadOnlyReason = "Loading";
        tab.LoadedCharacterCount = 0;
        tab.LoadedLineCount = 0;
        tab.CaretIndex = 0;
        tab.SelectionStart = 0;
        tab.SelectionLength = 0;
        tab.LastActivatedUtc = DateTime.UtcNow;
        tab.IsEditorBacked = false;
        tab.LoadBuffer = null;
        tab.StreamedToEditorCharacterCount = 0;
        tab.Mode = DocumentMode.LargePreview;
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

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            return await SaveDocumentAsync(tab, saveAs: false);
        }

        return true;
    }

    private async Task<bool> SaveDocumentAsync(DocumentTab? tab, bool saveAs)
    {
        if (tab is null)
        {
            return false;
        }

        if (tab.Mode == DocumentMode.LargePreview)
        {
            MessageBox.Show(
                this,
                "Large file mode is read-only. Open a smaller file to edit and save.",
                "Notepad",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        var encoding = tab.LineEndingLabel.Contains("UTF-8 BOM") ? new UTF8Encoding(true) : new UTF8Encoding(false);
        await File.WriteAllTextAsync(path!, tab.Text, encoding);
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
        {
            return;
        }

        if (tab.Mode == DocumentMode.LargePreview)
        {
            tab.PreviewTopLine = PreviewViewport.TopLine;
        }
        else if (tab.IsLoading)
        {
            tab.PreviewText = EditorTextBox.Text;
            tab.StreamedToEditorCharacterCount = tab.PreviewText.Length;
        }
        else if (tab.Mode == DocumentMode.Editable)
        {
            tab.Text = EditorTextBox.Text;
            tab.PreviewText = tab.Text;
            tab.LoadedCharacterCount = tab.Text.Length;
            tab.LoadedLineCount = CountVisibleLines(tab.Text);
        }

        if (tab.Mode != DocumentMode.LargePreview)
        {
            tab.CaretIndex = EditorTextBox.CaretIndex;
            tab.SelectionStart = EditorTextBox.SelectionStart;
            tab.SelectionLength = EditorTextBox.SelectionLength;
        }

        tab.LastActivatedUtc = DateTime.UtcNow;
    }

    private void PresentTab(DocumentTab tab, bool forceTextRefresh)
    {
        tab.LastActivatedUtc = DateTime.UtcNow;
        if (tab.Mode == DocumentMode.LargePreview)
        {
            PresentLargePreviewTab(tab);
            return;
        }

        ShowEditorSurface();
        var displayText = GetDisplayText(tab);

        if (forceTextRefresh || !string.Equals(EditorTextBox.Text, displayText, StringComparison.Ordinal))
        {
            _isInternalUpdate = true;
            EditorTextBox.Text = displayText;
            _isInternalUpdate = false;
        }

        tab.StreamedToEditorCharacterCount = EditorTextBox.Text.Length;

        EditorTextBox.IsReadOnly = tab.IsReadOnly;
        ConfigureWordWrap();
        EditorTextBox.CaretIndex = Math.Min(tab.CaretIndex, EditorTextBox.Text.Length);
        EditorTextBox.Select(
            Math.Min(tab.SelectionStart, EditorTextBox.Text.Length),
            Math.Min(tab.SelectionLength, Math.Max(0, EditorTextBox.Text.Length - Math.Min(tab.SelectionStart, EditorTextBox.Text.Length))));

        UpdateTitle();
        UpdateLoadingUi();
        UpdateStatusBar();
        RenderTabs();
    }

    private void RefreshActiveTabUi()
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        UpdateLoadingUi();
        UpdateStatusBar();
        UpdateTitle();
    }

    private static string GetDisplayText(DocumentTab tab)
    {
        if (tab.IsLoading)
        {
            if (tab.LoadBuffer is null)
            {
                return tab.PreviewText;
            }

            var snapshotLength = Math.Min(tab.PreviewText.Length, tab.LoadBuffer.Length);
            if (snapshotLength <= 0)
            {
                return string.Empty;
            }

            if (snapshotLength == tab.LoadBuffer.Length)
            {
                return tab.PreviewText;
            }

            return string.Concat(
                tab.PreviewText,
                tab.LoadBuffer.ToString(snapshotLength, tab.LoadBuffer.Length - snapshotLength));
        }

        return tab.Mode == DocumentMode.Editable ? tab.Text : tab.PreviewText;
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
            PreviewText = string.Empty,
            IsEditorBacked = true,
            Mode = DocumentMode.Editable,
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
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        CaptureActiveTabState();
        _activeTabIndex = index;
        var tab = _tabs[index];

        if (tab.Mode == DocumentMode.Editable && !tab.IsEditorBacked && !tab.IsDirty && !string.IsNullOrWhiteSpace(tab.Path))
        {
            _ = StartLoadingIntoTabAsync(tab, tab.Path!);
        }

        PresentTab(tab, forceTextRefresh: true);
        if (tab.Mode == DocumentMode.LargePreview)
        {
            PreviewViewport.Focus();
        }
        else
        {
            EditorTextBox.Focus();
        }

        TrimInactiveTabMemory();
    }

    private void TrimInactiveTabMemory()
    {
        if (_tabs.Count <= TabRetentionLimit)
        {
            return;
        }

        var retained = _tabs
            .OrderByDescending(tab => tab.Id == GetActiveTab()?.Id)
            .ThenByDescending(tab => tab.LastActivatedUtc)
            .Take(TabRetentionLimit)
            .Select(tab => tab.Id)
            .ToHashSet();

        foreach (var tab in _tabs)
        {
            if (retained.Contains(tab.Id) || tab.IsDirty || tab.IsLoading || string.IsNullOrWhiteSpace(tab.Path))
            {
                continue;
            }

            if (tab.Mode == DocumentMode.Editable)
            {
                tab.Text = string.Empty;
                tab.IsEditorBacked = false;
            }
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
                Height = 32,
                MinWidth = 120,
                MaxWidth = 220,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 0)
            };

            if (!isActive)
            {
                border.MouseEnter += (_, _) =>
                {
                    if (!isActive)
                    {
                        border.Background = (Brush)FindResource("TabHoverBrush");
                    }
                };
                border.MouseLeave += (_, _) =>
                {
                    if (!isActive)
                    {
                        border.Background = (Brush)FindResource("TabInactiveBrush");
                    }
                };
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
                    Foreground = (Brush)FindResource("TabForegroundBrush"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            titleButton.PreviewMouseLeftButtonDown += TabButton_OnPreviewMouseLeftButtonDown;

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
            closeButton.PreviewMouseLeftButtonDown += TabCloseButton_OnPreviewMouseLeftButtonDown;
            Grid.SetColumn(closeButton, 1);

            grid.Children.Add(titleButton);
            grid.Children.Add(closeButton);
            border.Child = grid;
            TabStripPanel.Children.Add(border);
        }
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
        DisposePreviewDocument(tab);
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            CreateNewTabAndActivate();
            return;
        }

        if (_activeTabIndex >= _tabs.Count)
        {
            _activeTabIndex = _tabs.Count - 1;
        }
        else if (index < _activeTabIndex)
        {
            _activeTabIndex--;
        }

        SwitchToTab(Math.Max(0, _activeTabIndex));
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
        if (!await ConfirmDiscardChangesAsync(tab))
        {
            return;
        }

        if (tab is null)
        {
            return;
        }

        CancelLoad(tab);
        DisposePreviewDocument(tab);
        var replacement = CreateNewDocumentTab();
        var index = _tabs.IndexOf(tab);
        _tabs[index] = replacement;
        _activeTabIndex = index;
        PresentTab(replacement, forceTextRefresh: true);
    }
}
