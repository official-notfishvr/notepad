using FastNote.Core;
using System.IO;
using System.Windows;

namespace FastNote.App;

public partial class MainWindow
{
    private const int QuickEditContextLines = 200;
    private const int QuickEditMinimumLines = 2_000;
    private const int QuickEditViewportMultiplier = 40;

    private bool ShouldUseLargeFilePreview(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists && fileInfo.Length >= LargeFileThresholdBytes;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadLargePreviewTabAsync(DocumentTab tab, string path, int loadVersion, CancellationToken cancellationToken)
    {
        var progress = new Progress<FileLoadProgress>(p => ApplyLargePreviewProgress(tab, loadVersion, p));
        var document = await FileDocument.OpenAsync(path, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (tab.LoadVersion != loadVersion)
        {
            document.Dispose();
            return;
        }

        tab.PreviewDocument = document;
        tab.PreviewProgressHandler = (_, progressUpdate) => ApplyLargePreviewProgress(tab, loadVersion, progressUpdate);
        document.ProgressChanged += tab.PreviewProgressHandler;

        tab.Text = string.Empty;
        tab.PreviewText = string.Empty;
        tab.LoadBuffer = null;
        tab.StreamedToEditorCharacterCount = 0;
        tab.IsEditorBacked = false;
        tab.IsReadOnly = true;
        tab.ReadOnlyReason = "Large file mode";
        tab.Mode = DocumentMode.LargePreview;
        tab.IsPartialEdit = false;
        tab.PartialEditStartLine = 0;
        tab.PartialEditLineCount = 0;
        tab.LineEndingLabel = "Mixed/Indexed";

        ApplyLargePreviewProgress(tab, loadVersion, document.LatestProgress);

        if (GetActiveTab()?.Id == tab.Id)
        {
            await Dispatcher.InvokeAsync(() => PresentLargePreviewTab(tab), System.Windows.Threading.DispatcherPriority.Background, cancellationToken);
        }
    }

    private void ApplyLargePreviewProgress(DocumentTab tab, int loadVersion, FileLoadProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (tab.LoadVersion != loadVersion)
            {
                return;
            }

            tab.LoadedCharacterCount = progress.BytesProcessed;
            tab.LoadedLineCount = progress.LinesDiscovered;
            tab.IsLoading = tab.PreviewDocument?.IsIndexComplete == false;
            tab.LoadingLabel = tab.IsLoading ? $"Indexing… {progress.PercentComplete:N0}%" : string.Empty;

            if (tab.PreviewDocument is not null)
            {
                tab.EncodingLabel = tab.PreviewDocument.EncodingName;
            }

            if (GetActiveTab()?.Id == tab.Id)
            {
                UpdateLoadingUi();
                UpdateStatusBar();
                UpdatePreviewScrollBar(tab);
            }

            RenderTabs();
        });
    }

    private void PresentLargePreviewTab(DocumentTab tab)
    {
        ShowPreviewSurface();
        PreviewViewport.Document = tab.PreviewDocument;
        PreviewViewport.ScrollToLine(tab.PreviewTopLine);
        UpdatePreviewScrollBar(tab);
        UpdateTitle();
        UpdateLoadingUi();
        UpdateStatusBar();
        RenderTabs();
        PreviewViewport.Focus();
    }

    private void ShowEditorSurface()
    {
        if (EditorTextBox.Visibility == Visibility.Visible)
        {
            return;
        }

        PreviewViewport.Visibility = Visibility.Collapsed;
        PreviewScrollBar.Visibility = Visibility.Collapsed;
        PreviewScrollBar.Maximum = 0;
        PreviewScrollBar.Value = 0;
        PreviewViewport.Document = null;
        EditorTextBox.Visibility = Visibility.Visible;
    }

    private void ShowPreviewSurface()
    {
        if (PreviewViewport.Visibility == Visibility.Visible)
        {
            return;
        }

        EditorTextBox.Visibility = Visibility.Collapsed;
        PreviewViewport.Visibility = Visibility.Visible;
        PreviewScrollBar.Visibility = Visibility.Visible;
        PreviewViewport.EditorFontSize = EditorTextBox.FontSize;
        PreviewViewport.WrapText = false;
    }

    private bool IsPreviewSurfaceActive()
    {
        return PreviewViewport.Visibility == Visibility.Visible;
    }

    private void DisposePreviewDocument(DocumentTab tab)
    {
        if (tab.PreviewDocument is null)
        {
            return;
        }

        if (ReferenceEquals(PreviewViewport.Document, tab.PreviewDocument))
        {
            PreviewViewport.Document = null;
        }

        if (tab.PreviewProgressHandler is not null)
        {
            tab.PreviewDocument.ProgressChanged -= tab.PreviewProgressHandler;
            tab.PreviewProgressHandler = null;
        }

        tab.PreviewDocument.Dispose();
        tab.PreviewDocument = null;
        tab.PreviewTopLine = 0;
    }

    private void UpdatePreviewScrollBar(DocumentTab? tab)
    {
        if (tab?.Mode != DocumentMode.LargePreview || tab.PreviewDocument is null || PreviewViewport.Visibility != Visibility.Visible)
        {
            PreviewScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        var visibleLineCount = Math.Max(1, PreviewViewport.VisibleLineCount);
        var estimatedTotalLines = Math.Max(visibleLineCount, tab.PreviewDocument.EstimateTotalLineCount());
        var maxTopLine = Math.Max(0, estimatedTotalLines - visibleLineCount);
        var viewportSize = Math.Min(estimatedTotalLines, visibleLineCount);
        var nextValue = Math.Clamp(PreviewViewport.TopLine, 0, maxTopLine);

        PreviewScrollBar.Visibility = Visibility.Visible;
        PreviewScrollBar.Minimum = 0;
        PreviewScrollBar.Maximum = Math.Max(0, maxTopLine);
        PreviewScrollBar.ViewportSize = viewportSize;
        PreviewScrollBar.LargeChange = Math.Max(1, visibleLineCount - 1);
        PreviewScrollBar.SmallChange = 1;

        if (Math.Abs(PreviewScrollBar.Value - nextValue) > 0.5)
        {
            PreviewScrollBar.Value = nextValue;
        }
    }

    private bool EnterQuickEditMode(DocumentTab tab)
    {
        if (tab.PreviewDocument is null)
        {
            return false;
        }

        var visibleLineCount = Math.Max(1, PreviewViewport.VisibleLineCount);
        var requestedLineCount = Math.Max(QuickEditMinimumLines, visibleLineCount * QuickEditViewportMultiplier);
        var startLine = Math.Max(0, tab.PreviewTopLine - QuickEditContextLines);
        var indexedLineCount = tab.PreviewDocument.IndexedLineCount;
        var safeLineCount = requestedLineCount;
        if (!tab.PreviewDocument.IsIndexComplete)
        {
            safeLineCount = (int)Math.Min(requestedLineCount, Math.Max(0, indexedLineCount - startLine - 1));
        }

        if (safeLineCount <= 0)
        {
            return false;
        }

        var lines = tab.PreviewDocument.ReadLines(startLine, safeLineCount);
        if (lines.Count == 0)
        {
            return false;
        }

        if (!tab.PreviewDocument.TryGetLineRangeOffsets(startLine, lines.Count, out var startOffset, out var endOffset))
        {
            return false;
        }

        var text = string.Join(Environment.NewLine, lines);
        tab.Text = text;
        tab.PreviewText = text;
        tab.IsLoading = false;
        tab.LoadingLabel = string.Empty;
        tab.IsReadOnly = false;
        tab.ReadOnlyReason = string.Empty;
        tab.IsEditorBacked = true;
        tab.Mode = DocumentMode.Editable;
        tab.IsPartialEdit = true;
        tab.PartialEditStartLine = startLine;
        tab.PartialEditLineCount = lines.Count;
        tab.PartialEditStartOffset = startOffset;
        tab.PartialEditEndOffset = endOffset;
        tab.PartialEditEncoding = tab.PreviewDocument.TextEncoding;
        tab.CaretIndex = 0;
        tab.SelectionStart = 0;
        tab.SelectionLength = 0;
        DisposePreviewDocument(tab);
        PresentTab(tab, forceTextRefresh: true);
        return true;
    }
}
