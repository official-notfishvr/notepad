using FastNote.Core;
using System.IO;
using System.Windows;

namespace FastNote.App;

public partial class MainWindow
{
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
            }

            if (!tab.IsLoading)
            {
                RenderTabs();
            }
        });
    }

    private void PresentLargePreviewTab(DocumentTab tab)
    {
        ShowPreviewSurface();
        PreviewViewport.Document = tab.PreviewDocument;
        PreviewViewport.ScrollToLine(tab.PreviewTopLine);
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
}
