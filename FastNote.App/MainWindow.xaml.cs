using FastNote.Core;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private LargeFileDocument? _document;
    private string? _currentPath;
    private bool _scrollSyncEnabled = true;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.Focus();
    }

    public async Task OpenFileAsync(string path)
    {
        try
        {
            await LoadDocumentAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FastNote", MessageBoxButton.OK, MessageBoxImage.Error);
            SetIdleState();
        }
    }

    private async Task LoadDocumentAsync(string path)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingProgressBar.Value = 0;
        LoadingTitleText.Text = "Indexing file";
        LoadingDetailText.Text = "Scanning line breaks and building the viewport index.";
        StatusText.Text = "Opening file...";
        LoadBadgeText.Text = "Loading";

        var progress = new Progress<FileLoadProgress>(UpdateProgress);
        var document = await LargeFileDocument.OpenAsync(path, progress);

        _document?.Dispose();
        _document = document;
        _currentPath = path;

        Viewport.Document = document;
        ConfigureScrollBar(document);
        UpdateHeader(document);
        StatusText.Text = $"Loaded in {document.IndexDuration.TotalMilliseconds:N0} ms.";
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadBadgeText.Text = $"Ready {document.IndexDuration.TotalMilliseconds:N0} ms";
        UpdateViewportStatus();
    }

    private void UpdateProgress(FileLoadProgress progress)
    {
        LoadingProgressBar.Value = progress.PercentComplete;
        LoadingDetailText.Text =
            $"{progress.PercentComplete:N0}%  •  {FormatBytes(progress.BytesProcessed)} / {FormatBytes(progress.TotalBytes)}  •  {progress.LinesDiscovered:N0} lines";
        StatusText.Text = $"Indexing {System.IO.Path.GetFileName(progress.Path)}...";
    }

    private void ConfigureScrollBar(LargeFileDocument document)
    {
        _scrollSyncEnabled = false;
        VerticalScrollBar.Minimum = 0;
        VerticalScrollBar.Maximum = Math.Max(0, document.LineCount - 1);
        VerticalScrollBar.ViewportSize = Math.Max(1, Viewport.VisibleLineCount);
        VerticalScrollBar.SmallChange = 1;
        VerticalScrollBar.LargeChange = Math.Max(1, Viewport.VisibleLineCount - 1);
        VerticalScrollBar.Value = 0;
        _scrollSyncEnabled = true;
        Viewport.ScrollToLine(0);
    }

    private void UpdateHeader(LargeFileDocument document)
    {
        FileNameText.Text = System.IO.Path.GetFileName(document.Path);
        FileMetaText.Text =
            $"{document.LineCount:N0} lines  •  {FormatBytes(document.FileSizeBytes)}  •  {document.EncodingName}";
        Title = $"{System.IO.Path.GetFileName(document.Path)} - FastNote";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }

    private void SetIdleState()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadBadgeText.Text = "Idle";
        StatusText.Text = "Ready.";
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        await OpenWithDialogAsync();
    }

    private async void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not null)
        {
            await OpenFileAsync(_currentPath);
        }
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files|*.txt;*.log;*.csv;*.json;*.xml;*.md|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await OpenWithDialogAsync();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void VerticalScrollBar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_scrollSyncEnabled || _document is null)
        {
            return;
        }

        Viewport.ScrollToLine((long)e.NewValue);
        UpdateViewportStatus();
    }

    private void Viewport_OnTopLineChanged(object? sender, EventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        _scrollSyncEnabled = false;
        VerticalScrollBar.ViewportSize = Math.Max(1, Viewport.VisibleLineCount);
        VerticalScrollBar.LargeChange = Math.Max(1, Viewport.VisibleLineCount - 1);
        VerticalScrollBar.Value = Math.Clamp(Viewport.TopLine, 0, Math.Max(0, _document.LineCount - 1));
        _scrollSyncEnabled = true;
        UpdateViewportStatus();
    }

    private void UpdateViewportStatus()
    {
        if (_document is null)
        {
            ViewportStatusText.Text = "Line 0 / 0";
            return;
        }

        var visibleStart = Math.Min(_document.LineCount, Viewport.TopLine + 1);
        var visibleEnd = Math.Min(_document.LineCount, Viewport.TopLine + Viewport.VisibleLineCount);
        ViewportStatusText.Text = $"Line {visibleStart:N0}-{visibleEnd:N0} / {_document.LineCount:N0}";
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
}
