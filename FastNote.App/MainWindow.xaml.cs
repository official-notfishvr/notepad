using Microsoft.Win32;
using FastNote.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int WordWrapSoftLimitCharacters = 300_000;
    private const long LargeFileThresholdBytes = 1L * 1024 * 1024;
    private const int TabRetentionLimit = 12;

    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loadTokens = [];
    private readonly DispatcherTimer _statusRefreshTimer;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private int _activeTabIndex = -1;

    private FontFamily _editorFontFamily = new("Consolas");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

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
        PreviewViewport.TopLineChanged += PreviewViewport_OnTopLineChanged;

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

        CaretText.Text = $"Ln {Math.Max(1, lineIndex + 1):N0}, Col {Math.Max(1, column):N0}";

        if (EditorTextBox.SelectionLength > 0)
        {
            var selLines = CountLineBreaks(EditorTextBox.SelectedText) + 1;
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
            "This file is too large to edit directly. It is shown in read-only mode.",
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

    private void FindNextInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var text = EditorTextBox.Text;
        var start = EditorTextBox.SelectionStart + EditorTextBox.SelectionLength;

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            index = FindWithRegex(text, query, start, forward: true);
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: true, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
            EditorTextBox.ScrollToLine(EditorTextBox.GetLineIndexFromCharacterIndex(index));
        }
    }

    private void FindPreviousInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var text = EditorTextBox.Text;
        var start = Math.Max(0, EditorTextBox.SelectionStart - 1);

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            index = FindWithRegex(text, query, start, forward: false);
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: false, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
            EditorTextBox.ScrollToLine(EditorTextBox.GetLineIndexFromCharacterIndex(index));
        }
    }

    private static int FindWithOptions(string text, string query, int start, bool forward, StringComparison comparison, bool wholeWord)
    {
        int index;
        if (forward)
        {
            index = text.IndexOf(query, start, comparison);
            if (index < 0)
            {
                index = text.IndexOf(query, comparison);
            }
        }
        else
        {
            index = start > 0 ? text.LastIndexOf(query, start, comparison) : -1;
            if (index < 0)
            {
                index = text.LastIndexOf(query, comparison);
            }
        }

        if (index >= 0 && wholeWord)
        {
            if ((index > 0 && char.IsLetterOrDigit(text[index - 1])) ||
                (index + query.Length < text.Length && char.IsLetterOrDigit(text[index + query.Length])))
            {
                return -1;
            }
        }

        return index;
    }

    private static int FindWithRegex(string text, string pattern, int start, bool forward)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.Multiline);
            if (forward)
            {
                var match = regex.Match(text, start);
                if (!match.Success)
                {
                    match = regex.Match(text);
                }

                return match.Success ? match.Index : -1;
            }
            else
            {
                var matches = regex.Matches(text[..start]);
                return matches.Count > 0 ? matches[^1].Index : -1;
            }
        }
        catch
        {
            return -1;
        }
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
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

    private void OpenFindPanel(bool showReplace)
    {
        FindPanel.Visibility = Visibility.Visible;
        _replaceVisible = showReplace;
        ReplaceRowPanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
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
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        EditorTextBox.Undo();
    }

    private void RedoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        EditorTextBox.Redo();
    }

    private void CutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

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
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

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

        if (tab.Mode == DocumentMode.LargePreview)
        {
            MessageBox.Show(this, "Word wrap is not available in large file mode.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void FindButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: false);
    }

    private void FindAndReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: true);
    }

    private void GoToMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

        var dialog = new GoToLineDialog(EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex) + 1)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.LineNumber > 0)
        {
            var targetLine = dialog.LineNumber - 1;
            var lineCount = EditorTextBox.LineCount;
            targetLine = Math.Clamp(targetLine, 0, lineCount - 1);
            var charIndex = EditorTextBox.GetCharacterIndexFromLineIndex(targetLine);
            EditorTextBox.CaretIndex = charIndex;
            EditorTextBox.ScrollToLine(targetLine);
            EditorTextBox.Focus();
        }
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

    private void TabButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: int index })
        {
            SwitchToTab(index);
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

    private void CloseFindPanelButton_OnClick(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        _replaceVisible = false;
        ReplaceRowPanel.Visibility = Visibility.Collapsed;
        EditorTextBox.Focus();
    }

    private void FindTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(FindTextBox.Text))
        {
            FindNextInternal();
        }
    }

    private void FindTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPreviousInternal();
            }
            else
            {
                FindNextInternal();
            }

            e.Handled = true;
        }
    }

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e) => FindNextInternal();
    private void FindPreviousButton_OnClick(object sender, RoutedEventArgs e) => FindPreviousInternal();

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
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
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        string newText;
        if (UseRegexCheckBox.IsChecked == true)
        {
            try
            {
                newText = Regex.Replace(EditorTextBox.Text, query, ReplaceTextBox.Text, RegexOptions.Multiline);
            }
            catch
            {
                MessageBox.Show(this, "Invalid regular expression.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            var options = MatchCaseCheckBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            newText = Regex.Replace(EditorTextBox.Text, Regex.Escape(query), ReplaceTextBox.Text, options);
        }

        var caretPos = EditorTextBox.CaretIndex;
        EditorTextBox.Text = newText;
        EditorTextBox.CaretIndex = Math.Min(caretPos, newText.Length);
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
            DisposePreviewDocument(tab);
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

        var wasDirty = tab.IsDirty;
        tab.Text = EditorTextBox.Text;
        tab.PreviewText = tab.Text;
        tab.IsDirty = true;
        tab.Title = string.IsNullOrWhiteSpace(tab.Path) ? "Untitled" : Path.GetFileName(tab.Path);
        tab.LoadedCharacterCount = tab.Text.Length;
        tab.LoadedLineCount = CountVisibleLines(tab.Text);
        tab.LineEndingLabel = DetectLineEnding(tab.Text);

        if (!wasDirty)
        {
            RenderTabs();
        }

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

    private void PreviewViewport_OnTopLineChanged(object? sender, EventArgs e)
    {
        var tab = GetActiveTab();
        if (tab?.Mode != DocumentMode.LargePreview)
        {
            return;
        }

        tab.PreviewTopLine = PreviewViewport.TopLine;
        UpdateStatusBar();
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
        public StringBuilder? LoadBuffer { get; set; }
        public int StreamedToEditorCharacterCount { get; set; }
        public FileDocument? PreviewDocument { get; set; }
        public EventHandler<FileLoadProgress>? PreviewProgressHandler { get; set; }
        public long PreviewTopLine { get; set; }

        public string DisplayTitle
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(Path) ? Title : System.IO.Path.GetFileName(Path);
                var suffix = IsLoading ? "…" : IsDirty ? " ●" : string.Empty;
                return name + suffix;
            }
        }
    }
}

public sealed class GoToLineDialog : Window
{
    private readonly TextBox _lineBox;
    public int LineNumber { get; private set; }

    public GoToLineDialog(int currentLine)
    {
        Title = "Go To Line";
        Width = 320;
        Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var bg = Application.Current.Resources["PopupBackgroundBrush"] as Brush ?? Brushes.DarkGray;
        var fg = Application.Current.Resources["MenuForegroundBrush"] as Brush ?? Brushes.White;

        Background = bg;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Line number:",
            Foreground = fg,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(label, 0);

        _lineBox = new TextBox
        {
            Text = currentLine.ToString(),
            FontSize = 13,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _lineBox.SelectAll();
        Grid.SetRow(_lineBox, 1);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttonPanel, 2);

        var goButton = new Button
        {
            Content = "Go",
            Width = 72,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        goButton.Click += (_, _) =>
        {
            if (int.TryParse(_lineBox.Text, out var n))
            {
                LineNumber = n;
                DialogResult = true;
            }
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 72,
            Height = 28,
            IsCancel = true
        };

        buttonPanel.Children.Add(goButton);
        buttonPanel.Children.Add(cancelButton);

        grid.Children.Add(label);
        grid.Children.Add(_lineBox);
        grid.Children.Add(buttonPanel);

        Content = grid;

        Loaded += (_, _) => _lineBox.Focus();
    }
}

public sealed class FontPickerDialog : Window
{
    public FontFamily SelectedFontFamily { get; private set; }
    public FontStyle SelectedFontStyle { get; private set; }
    public FontWeight SelectedFontWeight { get; private set; }
    public double SelectedFontSize { get; private set; }

    private readonly ListBox _fontList;
    private readonly ListBox _styleList;
    private readonly ListBox _sizeList;
    private readonly TextBlock _preview;

    public FontPickerDialog(FontFamily family, FontStyle style, FontWeight weight, double size)
    {
        SelectedFontFamily = family;
        SelectedFontStyle = style;
        SelectedFontWeight = weight;
        SelectedFontSize = size;

        Title = "Font";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var bg = Application.Current.Resources["PopupBackgroundBrush"] as Brush ?? Brushes.DarkGray;
        var fg = Application.Current.Resources["MenuForegroundBrush"] as Brush ?? Brushes.White;
        Background = bg;

        var outer = new Grid { Margin = new Thickness(14) };
        outer.RowDefinitions.Add(new RowDefinition());
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var listsGrid = new Grid();
        listsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        listsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        listsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        _fontList = CreateListBox(fg, bg);
        _styleList = CreateListBox(fg, bg);
        _sizeList = CreateListBox(fg, bg);

        foreach (var f in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            _fontList.Items.Add(f.Source);
        }

        foreach (var s in new[] { "Regular", "Italic", "Bold", "Bold Italic" })
        {
            _styleList.Items.Add(s);
        }

        foreach (var s in new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72 })
        {
            _sizeList.Items.Add(s.ToString());
        }

        _fontList.SelectedItem = family.Source;
        _styleList.SelectedItem = weight == FontWeights.Bold && style == FontStyles.Italic ? "Bold Italic"
            : weight == FontWeights.Bold ? "Bold"
            : style == FontStyles.Italic ? "Italic"
            : "Regular";
        _sizeList.SelectedItem = ((int)size).ToString();

        _fontList.SelectionChanged += (_, _) => UpdatePreview();
        _styleList.SelectionChanged += (_, _) => UpdatePreview();
        _sizeList.SelectionChanged += (_, _) => UpdatePreview();

        Grid.SetColumn(_fontList, 0);
        Grid.SetColumn(_styleList, 1);
        Grid.SetColumn(_sizeList, 2);
        listsGrid.Children.Add(_fontList);
        listsGrid.Children.Add(_styleList);
        listsGrid.Children.Add(_sizeList);

        _preview = new TextBlock
        {
            Text = "AaBbCcDdEe 0123456789",
            Foreground = fg,
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_preview, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(buttons, 2);

        var ok = new Button { Content = "OK", Width = 72, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 72, Height = 28, IsCancel = true };

        ok.Click += (_, _) =>
        {
            ApplySelection();
            DialogResult = true;
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        outer.Children.Add(listsGrid);
        outer.Children.Add(_preview);
        outer.Children.Add(buttons);

        Content = outer;
        UpdatePreview();
    }

    private static ListBox CreateListBox(Brush fg, Brush bg)
    {
        return new ListBox
        {
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12
        };
    }

    private void UpdatePreview()
    {
        if (_fontList.SelectedItem is string fontName)
        {
            _preview.FontFamily = new FontFamily(fontName);
        }

        if (_styleList.SelectedItem is string styleName)
        {
            _preview.FontStyle = styleName.Contains("Italic") ? FontStyles.Italic : FontStyles.Normal;
            _preview.FontWeight = styleName.Contains("Bold") ? FontWeights.Bold : FontWeights.Normal;
        }

        if (_sizeList.SelectedItem is string sizeName && double.TryParse(sizeName, out var s))
        {
            _preview.FontSize = s;
        }
    }

    private void ApplySelection()
    {
        if (_fontList.SelectedItem is string fontName)
        {
            SelectedFontFamily = new FontFamily(fontName);
        }

        if (_styleList.SelectedItem is string styleName)
        {
            SelectedFontStyle = styleName.Contains("Italic") ? FontStyles.Italic : FontStyles.Normal;
            SelectedFontWeight = styleName.Contains("Bold") ? FontWeights.Bold : FontWeights.Normal;
        }

        if (_sizeList.SelectedItem is string sizeName && double.TryParse(sizeName, out var s))
        {
            SelectedFontSize = s;
        }
    }
}
