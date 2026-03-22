using FastNote.Core;
using ICSharpCode.AvalonEdit.Document;

namespace FastNote.App;

public partial class MainWindow
{
    private enum AppThemeMode
    {
        Light,
        Dark,
    }

    private sealed class DocumentTab
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "Untitled";
        public string? Path { get; set; }
        public string Text { get; set; } = string.Empty;
        public TextDocument? EditorDocument { get; set; }
        public bool IsDirty { get; set; }
        public bool WordWrapEnabled { get; set; }
        public int CaretIndex { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public string EncodingLabel { get; set; } = "UTF-8";
        public bool IsLoading { get; set; }
        public string LoadingLabel { get; set; } = string.Empty;
        public int LoadVersion { get; set; }
        public long LoadedCharacterCount { get; set; }
        public long LoadedLineCount { get; set; } = 1;
        public string LineEndingLabel { get; set; } = "Windows (CRLF)";
        public DateTime LastActivatedUtc { get; set; }
        public int StreamedToEditorCharacterCount { get; set; }
        public bool IsEditorBacked { get; set; }
        public bool IsViewportBacked { get; set; }
        public FileDocument? VirtualDocument { get; set; }
        public bool IsTextCacheReady { get; set; }
        public bool IsHydratingText { get; set; }
        public bool AutoActivateEditorWhenReady { get; set; }
        public bool IsMarkdownPreviewEnabled { get; set; }
        public string? MarkdownPreviewCacheKey { get; set; }

        public string DisplayTitle
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(Path) ? Title : System.IO.Path.GetFileName(Path);
                var suffix =
                    IsLoading ? " \u2026"
                    : IsDirty ? " \u25CF"
                    : string.Empty;
                return name + suffix;
            }
        }
    }
}
