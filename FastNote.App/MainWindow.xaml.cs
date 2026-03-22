using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int WordWrapSoftLimitCharacters = 300_000;
    private const long LargeFileThresholdBytes = 8L * 1024 * 1024;
    private const int LargeFilePreviewCharacterLimit = 1_000_000;
    private const int LargeFilePreviewLineLimit = 25_000;
    private const int TabRetentionLimit = 12;

    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loadTokens = [];
    private readonly DispatcherTimer _statusRefreshTimer;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private int _activeTabIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(AppThemeMode.Dark);
        UpdateWindowButtons();

        _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _statusRefreshTimer.Tick += (_, _) => RefreshActiveTabUi();
        _statusRefreshTimer.Start();

        CreateNewTabAndActivate();
        EditorTextBox.Focus();
    }

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
        }
        catch (Exception ex)
        {
            if (GetActiveTab()?.Id == tab.Id)
            {
                MessageBox.Show(this, ex.Message, "FastNote", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            tab.IsLoading = false;
            tab.LoadingLabel = string.Empty;
            tab.ReadOnlyReason = "Load failed";
            if (GetActiveTab()?.Id == tab.Id)
            {
                RefreshActiveTabUi();
            }
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

        var previewBuilder = new StringBuilder(isLargeFile ? Math.Min(LargeFilePreviewCharacterLimit, 256 * 1024) : (int)Math.Min(fileInfo.Length, 512 * 1024));
        var fullBuilder = new StringBuilder((int)Math.Min(fileInfo.Length, 1024 * 1024));
        var buffer = new char[64 * 1024];
        var appendedLines = 0;
        var totalLines = 0L;
        var totalCharacters = 0L;
        var previewClosed = false;
        var lastUiRefreshUtc = DateTime.MinValue;

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

            if (!previewClosed)
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (appendedLines >= LargeFilePreviewLineLimit || previewBuilder.Length >= LargeFilePreviewCharacterLimit)
                    {
                        previewClosed = true;
                        break;
                    }

                    var ch = chunk[i];
                    previewBuilder.Append(ch);
                    if (ch == '\n')
                    {
                        appendedLines++;
                    }
                }
            }

            tab.LoadedCharacterCount = totalCharacters;
            tab.LoadedLineCount = totalLines;
            tab.EncodingLabel = ToEncodingLabel(reader.CurrentEncoding);
            tab.LoadingLabel = $"Loading... {stream.Position * 100 / Math.Max(1, stream.Length):N0}%";

            if (tab.Mode == DocumentMode.LargePreview)
            {
                tab.PreviewText = previewBuilder.ToString();
                tab.ReadOnlyReason = "Large file mode";
            }

            if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
            {
                var now = DateTime.UtcNow;
                if ((now - lastUiRefreshUtc).TotalMilliseconds >= 140)
                {
                    lastUiRefreshUtc = now;
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
                            {
                                PresentTab(tab, forceTextRefresh: tab.Mode == DocumentMode.LargePreview);
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

        tab.Text = fullBuilder.ToString();
        tab.PreviewText = tab.Text;
        tab.IsEditorBacked = true;
        tab.IsReadOnly = false;
        tab.ReadOnlyReason = string.Empty;
        tab.Mode = DocumentMode.Editable;

        if (GetActiveTab()?.Id == tab.Id && tab.LoadVersion == loadVersion)
        {
            await Dispatcher.InvokeAsync(() => PresentTab(tab, forceTextRefresh: true), DispatcherPriority.Background, cancellationToken);
        }

        TrimInactiveTabMemory();
    }

    private void ResetTabForLoad(DocumentTab tab, string path)
    {
        tab.Path = path;
        tab.Title = Path.GetFileName(path);
        tab.Text = string.Empty;
        tab.PreviewText = string.Empty;
        tab.IsDirty = false;
        tab.IsLoading = true;
        tab.LoadingLabel = "Loading...";
        tab.IsReadOnly = true;
        tab.ReadOnlyReason = "Loading";
        tab.LoadedCharacterCount = 0;
        tab.LoadedLineCount = 0;
        tab.CaretIndex = 0;
        tab.SelectionStart = 0;
        tab.SelectionLength = 0;
        tab.LastActivatedUtc = DateTime.UtcNow;
        tab.IsEditorBacked = false;
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
            "Do you want to save changes to this file?",
            "FastNote",
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
                "Large file mode is read-only in this build. Open a smaller file to edit and save normally.",
                "FastNote",
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
                Filter = "Text files|*.txt;*.log;*.md;*.json;*.xml|All files|*.*",
                FileName = string.IsNullOrWhiteSpace(path) ? "Untitled.txt" : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        await File.WriteAllTextAsync(path!, tab.Text, new UTF8Encoding(false));
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

        if (tab.Mode == DocumentMode.Editable && !tab.IsLoading)
        {
            tab.Text = EditorTextBox.Text;
            tab.PreviewText = tab.Text;
            tab.LoadedCharacterCount = tab.Text.Length;
            tab.LoadedLineCount = CountVisibleLines(tab.Text);
        }

        tab.CaretIndex = EditorTextBox.CaretIndex;
        tab.SelectionStart = EditorTextBox.SelectionStart;
        tab.SelectionLength = EditorTextBox.SelectionLength;
        tab.LastActivatedUtc = DateTime.UtcNow;
    }

    private void PresentTab(DocumentTab tab, bool forceTextRefresh)
    {
        tab.LastActivatedUtc = DateTime.UtcNow;
        var displayText = tab.Mode == DocumentMode.Editable ? tab.Text : tab.PreviewText;

        if (forceTextRefresh || !string.Equals(EditorTextBox.Text, displayText, StringComparison.Ordinal))
        {
            _isInternalUpdate = true;
            EditorTextBox.Text = displayText;
            _isInternalUpdate = false;
        }

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

        if (tab.Mode == DocumentMode.LargePreview && tab.IsLoading)
        {
            PresentTab(tab, forceTextRefresh: true);
            return;
        }

        UpdateLoadingUi();
        UpdateStatusBar();
        UpdateTitle();
        RenderTabs();
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
        EditorTextBox.Focus();
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
            else if (tab.PreviewText.Length > LargeFilePreviewCharacterLimit)
            {
                tab.PreviewText = tab.PreviewText[..LargeFilePreviewCharacterLimit];
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
                Width = 244,
                Height = 30,
                Background = (Brush)FindResource(isActive ? "TabActiveBrush" : "TabInactiveBrush"),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                Padding = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleButton = new Button
            {
                Style = (Style)FindResource("TabStripButtonStyle"),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = i,
                Content = new TextBlock
                {
                    Text = tab.DisplayTitle,
                    Foreground = (Brush)FindResource("TabForegroundBrush"),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            titleButton.PreviewMouseLeftButtonDown += TabButton_OnPreviewMouseLeftButtonDown;

            var closeButton = new Button
            {
                Style = (Style)FindResource("TabCloseButtonStyle"),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = tab.Id,
                Content = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 12,
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
            Filter = "Text files|*.txt;*.log;*.md;*.json;*.xml|All files|*.*",
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
        var replacement = CreateNewDocumentTab();
        var index = _tabs.IndexOf(tab);
        _tabs[index] = replacement;
        _activeTabIndex = index;
        PresentTab(replacement, forceTextRefresh: true);
    }

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
        LoadingText.Text = isLoading ? tab!.LoadingLabel : "Loading...";
        EncodingText.Text = tab?.EncodingLabel ?? "UTF-8";
    }

    private void UpdateStatusBar()
    {
        var tab = GetActiveTab();
        var lineIndex = EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex);
        var lineStart = EditorTextBox.GetCharacterIndexFromLineIndex(lineIndex);
        var column = EditorTextBox.CaretIndex - lineStart + 1;

        CaretText.Text = $"Ln {Math.Max(1, lineIndex + 1):N0}, Col {Math.Max(1, column):N0}";
        CharacterCountText.Text = $"{(tab?.LoadedCharacterCount ?? EditorTextBox.Text.Length):N0} characters";
        DocumentModeText.Text = tab?.Mode == DocumentMode.LargePreview
            ? "Large file mode"
            : (tab?.WordWrapEnabled == true ? "Word wrap" : "Plain text");
        LineEndingText.Text = tab?.LineEndingLabel ?? DetectLineEnding(EditorTextBox.Text);
        WordWrapCheckGlyph.Opacity = tab?.WordWrapEnabled == true ? 0.8 : 0;
        StatusBarCheckGlyph.Opacity = _statusBarVisible ? 0.8 : 0;
        UpdateZoomStatus();
    }

    private void ConfigureWordWrap()
    {
        var enabled = GetActiveTab()?.WordWrapEnabled == true;
        EditorTextBox.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorTextBox.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void UpdateWindowButtons()
    {
        MaxRestoreIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateZoomStatus()
    {
        var percent = EditorTextBox.FontSize / DefaultEditorFontSize * 100;
        ZoomText.Text = $"{percent:N0}%";
    }

    private void ZoomBy(int delta)
    {
        EditorTextBox.FontSize = Math.Clamp(EditorTextBox.FontSize + delta, 10, 30);
        UpdateZoomStatus();
    }

    private static void WrapSelection(TextBox textBox, string prefix, string suffix)
    {
        if (textBox.SelectionLength > 0)
        {
            var start = textBox.SelectionStart;
            var length = textBox.SelectionLength;
            var content = textBox.SelectedText;
            textBox.SelectedText = prefix + content + suffix;
            textBox.Select(start + prefix.Length, length);
            return;
        }

        var lineIndex = textBox.GetLineIndexFromCharacterIndex(textBox.CaretIndex);
        var lineStart = textBox.GetCharacterIndexFromLineIndex(lineIndex);
        textBox.Select(lineStart, 0);
        textBox.SelectedText = prefix;
        textBox.CaretIndex = lineStart + prefix.Length;
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
            "This tab is in large file mode. Editing is disabled to keep tab switching and loading responsive.",
            "FastNote",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        EditMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
        HeadingPopup.IsOpen = false;
        ListPopup.IsOpen = false;
        TablePopup.IsOpen = false;
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            CreateNewTabAndActivate();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            await OpenWithDialogAsync();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveDocumentAsync(GetActiveTab(), saveAs: false);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            FindButton_OnClick(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            e.Handled = true;
            FindButton_OnClick(this, new RoutedEventArgs());
            if (!_replaceVisible)
            {
                ToggleReplaceButton_OnClick(this, new RoutedEventArgs());
            }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            CloseTabButton_OnClick(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Add || e.Key == Key.OemPlus))
        {
            e.Handled = true;
            ZoomBy(1);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Subtract || e.Key == Key.OemMinus))
        {
            e.Handled = true;
            ZoomBy(-1);
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

        base.OnPreviewKeyDown(e);
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();

    private async void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (!string.IsNullOrWhiteSpace(tab?.Path))
        {
            await StartLoadingIntoTabAsync(tab, tab.Path!);
        }
    }

    private async void NewFileMenuItem_OnClick(object sender, RoutedEventArgs e) => await NewDocumentAsync();
    private async void SaveMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveDocumentAsync(GetActiveTab(), saveAs: false);
    private async void SaveAsMenuItem_OnClick(object sender, RoutedEventArgs e) => await SaveDocumentAsync(GetActiveTab(), saveAs: true);

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

    private void UndoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        EditorTextBox.Undo();
    }

    private void CutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        EditorTextBox.Cut();
    }

    private void CopyMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.Copy();

    private void PasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        EditorTextBox.Paste();
    }

    private void SelectAllMenuItem_OnClick(object sender, RoutedEventArgs e) => EditorTextBox.SelectAll();

    private void WordWrapMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        if (tab.Mode == DocumentMode.LargePreview)
        {
            MessageBox.Show(
                this,
                "Word wrap is disabled in large file mode to avoid full-buffer reflow freezes.",
                "FastNote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ViewMenuPopup.IsOpen = false;
            return;
        }

        if (!tab.WordWrapEnabled && EditorTextBox.Text.Length > WordWrapSoftLimitCharacters)
        {
            MessageBox.Show(
                this,
                "Word wrap is disabled for very large editable documents because WPF reflows the entire buffer and stalls the window.",
                "FastNote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ViewMenuPopup.IsOpen = false;
            return;
        }

        tab.WordWrapEnabled = !tab.WordWrapEnabled;
        ConfigureWordWrap();
        UpdateStatusBar();
        ViewMenuPopup.IsOpen = false;
    }

    private void ZoomInMenuItem_OnClick(object sender, RoutedEventArgs e) => ZoomBy(1);
    private void ZoomOutMenuItem_OnClick(object sender, RoutedEventArgs e) => ZoomBy(-1);

    private void RestoreZoomMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
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

    private void LightThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Light);
    private void DarkThemeMenuItem_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(AppThemeMode.Dark);
    private void ThemeToggleToolbarButton_OnClick(object sender, RoutedEventArgs e) => ApplyTheme(_themeMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark);

    private void ApplyTheme(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        var dark = themeMode == AppThemeMode.Dark;

        SetBrush("WindowBackgroundBrush", dark ? "#FF171520" : "#FFF3F1F8");
        SetBrush("ChromeBrush", dark ? "#FF160432" : "#FFEEE9FA");
        SetBrush("MenuBrush", dark ? "#FF241E3B" : "#FFF5F2FB");
        SetBrush("SurfaceBrush", dark ? "#FF2B2B2B" : "#FFFFFFFF");
        SetBrush("SurfaceRaisedBrush", dark ? "#FF343434" : "#FFF3F1F6");
        SetBrush("EditorBackgroundBrush", dark ? "#FF292929" : "#FFFFFFFF");
        SetBrush("EditorForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("EditorMutedBrush", dark ? "#FFA7A0B8" : "#FF706A7E");
        SetBrush("BorderBrush", dark ? "#FF3E3657" : "#FFD8D0E8");
        SetBrush("DividerBrush", dark ? "#FF4A425F" : "#FFE2DCEC");
        SetBrush("TabActiveBrush", dark ? "#FF272046" : "#FFFFFFFF");
        SetBrush("TabInactiveBrush", dark ? "#00000000" : "#00000000");
        SetBrush("TabForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("AccentBrush", dark ? "#FF7D68FF" : "#FF5E49D8");
        SetBrush("AccentSoftBrush", dark ? "#FF342B56" : "#FFE6E1FA");
        SetBrush("StatusBrush", dark ? "#FF241E3B" : "#FFF5F2FB");
        SetBrush("StatusForegroundBrush", dark ? "#FFE2DDF5" : "#FF4D475C");
        SetBrush("MenuForegroundBrush", dark ? "#FFF1F1F1" : "#FF1E1D22");
        SetBrush("MenuSelectionBrush", dark ? "#FF3F365A" : "#FFE5DFF1");
        SetBrush("PopupBackgroundBrush", dark ? "#FF2F2F2F" : "#FFFFFFFF");
        SetBrush("PopupBorderBrush", dark ? "#FF3C3C3C" : "#FFD7D1E2");
        SetBrush("InputBackgroundBrush", dark ? "#FF343434" : "#FFF7F6FA");
        SetBrush("InputBorderBrush", dark ? "#FF5A5A5A" : "#FFCFC9DB");
        SetBrush("InputFocusBrush", dark ? "#FF8A78FF" : "#FF6A57E6");
        SetBrush("ButtonHoverBrush", dark ? "#FF4B4364" : "#FFE7E0F3");
        SetBrush("ButtonPressedBrush", dark ? "#FF5A5176" : "#FFD9D0EB");
        SetBrush("ScrollThumbBrush", dark ? "#FF6B657A" : "#FFC3BCD6");
        SetBrush("ScrollThumbHoverBrush", dark ? "#FF867F97" : "#FF9E95B7");
        SetBrush("ScrollTrackBrush", dark ? "#18211D2A" : "#10000000");
    }

    private static void SetBrush(string key, string hex)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void HeadingButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "# ", string.Empty);
    private void BulletListButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "- ", string.Empty);
    private void NumberedListButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "1. ", string.Empty);
    private void ChecklistButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "[ ] ", string.Empty);
    private void BoldButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "**", "**");
    private void ItalicButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "_", "_");
    private void StrikeButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "~~", "~~");
    private void LinkButton_OnClick(object sender, RoutedEventArgs e) => WrapSelection(EditorTextBox, "[", "](https://)");
    private void TableButton_OnClick(object sender, RoutedEventArgs e) => InsertTextAtCaret("| Column 1 | Column 2 |\r\n| --- | --- |\r\n| Value | Value |\r\n");

    private void ClearFormattingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        if (EditorTextBox.SelectionLength > 0)
        {
            var cleaned = EditorTextBox.SelectedText.Replace("**", string.Empty).Replace("_", string.Empty).Replace("~~", string.Empty);
            EditorTextBox.SelectedText = cleaned;
        }
    }

    private void FindButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void NewTabButton_OnClick(object sender, RoutedEventArgs e) => CreateNewTabAndActivate();

    private async void CloseTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is not null)
        {
            await CloseTabAsync(tab.Id);
        }
    }

    private async void NewWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            Process.Start(Environment.ProcessPath!);
        }

        await Task.CompletedTask;
        FileMenuPopup.IsOpen = false;
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
    private void HeadingMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(HeadingPopup);
    private void ListMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(ListPopup);
    private void TableMenuButton_OnClick(object sender, RoutedEventArgs e) => TogglePopup(TablePopup);

    private void TogglePopup(System.Windows.Controls.Primitives.Popup popup)
    {
        var shouldOpen = !popup.IsOpen;
        CloseMenus();
        popup.IsOpen = shouldOpen;
    }

    private void InsertTimeDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        InsertTextAtCaret(DateTime.Now.ToString("HH:mm M/d/yyyy"));
        EditMenuPopup.IsOpen = false;
    }

    private void FontMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Font picker is not implemented yet.", "FastNote", MessageBoxButton.OK, MessageBoxImage.Information);
        EditMenuPopup.IsOpen = false;
    }

    private void InsertTitleButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "# ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubtitleButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "## ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertHeadingButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubheadingButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "#### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSectionButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "##### ", string.Empty); HeadingPopup.IsOpen = false; }
    private void InsertSubsectionButton_OnClick(object sender, RoutedEventArgs e) { WrapSelection(EditorTextBox, "###### ", string.Empty); HeadingPopup.IsOpen = false; }

    private void InsertBodyButton_OnClick(object sender, RoutedEventArgs e)
    {
        HeadingPopup.IsOpen = false;
        FileMenuPopup.IsOpen = false;
    }

    private void TabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            SwitchToTab(index);
        }
    }

    private void TabButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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

    private async void TabCloseButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: Guid tabId })
        {
            await CloseTabAsync(tabId);
        }
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
        }

        _statusRefreshTimer.Stop();
        base.OnClosing(e);
    }

    private void EditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalUpdate)
        {
            return;
        }

        var tab = GetActiveTab();
        if (tab is null || tab.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        tab.Text = EditorTextBox.Text;
        tab.PreviewText = tab.Text;
        tab.IsDirty = true;
        tab.Title = string.IsNullOrWhiteSpace(tab.Path) ? "Untitled" : Path.GetFileName(tab.Path);
        tab.LoadedCharacterCount = tab.Text.Length;
        tab.LoadedLineCount = CountVisibleLines(tab.Text);
        tab.LineEndingLabel = DetectLineEnding(tab.Text);
        RefreshActiveTabUi();
    }

    private void EditorTextBox_OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab is null)
        {
            return;
        }

        tab.CaretIndex = EditorTextBox.CaretIndex;
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

    private void CloseFindPanelButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _replaceVisible = false;
        ReplaceTextBox.Visibility = Visibility.Collapsed;
        ReplaceButtonPanel.Visibility = Visibility.Collapsed;
        EditorTextBox.Focus();
    }

    private void ToggleReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        _replaceVisible = !_replaceVisible;
        ReplaceTextBox.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceButtonPanel.Visibility = _replaceVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_replaceVisible)
        {
            ReplaceTextBox.Focus();
        }
    }

    private void FindTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            FindNextInternal();
        }
    }

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e) => FindNextInternal();
    private void FindPreviousButton_OnClick(object sender, RoutedEventArgs e) => FindPreviousInternal();

    private void FindNextInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var start = EditorTextBox.SelectionStart + EditorTextBox.SelectionLength;
        var index = EditorTextBox.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = EditorTextBox.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
        }
    }

    private void FindPreviousInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var start = Math.Max(0, EditorTextBox.SelectionStart - 1);
        var index = EditorTextBox.Text.LastIndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = EditorTextBox.Text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
        }
    }

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (EditorTextBox.SelectionLength > 0 && string.Equals(EditorTextBox.SelectedText, query, StringComparison.OrdinalIgnoreCase))
        {
            EditorTextBox.SelectedText = ReplaceTextBox.Text;
        }

        FindNextInternal();
    }

    private void ReplaceAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        EditorTextBox.Text = Regex.Replace(
            EditorTextBox.Text,
            Regex.Escape(query),
            ReplaceTextBox.Text,
            RegexOptions.IgnoreCase);
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

        if (value.Contains('\n'))
        {
            return "Unix (LF)";
        }

        return "Windows (CRLF)";
    }

    private static string ToEncodingLabel(Encoding encoding)
    {
        return encoding.EncodingName.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            ? "UTF-8"
            : encoding.EncodingName;
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
                case StackPanel { Name: "TabStripPanel" }:
                    return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private enum AppThemeMode
    {
        Light,
        Dark
    }

    private enum DocumentMode
    {
        Editable,
        LargePreview
    }

    private sealed class DocumentTab
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "Untitled";
        public string? Path { get; set; }
        public string Text { get; set; } = string.Empty;
        public string PreviewText { get; set; } = string.Empty;
        public bool IsDirty { get; set; }
        public bool WordWrapEnabled { get; set; }
        public int CaretIndex { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public string EncodingLabel { get; set; } = "UTF-8";
        public bool IsLoading { get; set; }
        public string LoadingLabel { get; set; } = string.Empty;
        public int LoadVersion { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsEditorBacked { get; set; }
        public string ReadOnlyReason { get; set; } = string.Empty;
        public long LoadedCharacterCount { get; set; }
        public long LoadedLineCount { get; set; } = 1;
        public string LineEndingLabel { get; set; } = "Windows (CRLF)";
        public DateTime LastActivatedUtc { get; set; }
        public DocumentMode Mode { get; set; } = DocumentMode.Editable;

        public string DisplayTitle
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(Path) ? Title : System.IO.Path.GetFileName(Path);
                var suffix = IsLoading ? "..." : IsDirty ? " •" : string.Empty;
                return name + suffix;
            }
        }
    }
}
