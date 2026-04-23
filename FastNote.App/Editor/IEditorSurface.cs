using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App.Editor;

public interface IEditorSurface
{
    event EventHandler? TextChanged;
    event EventHandler? SelectionChanged;
    event EventHandler? CaretPositionChanged;
    event MouseButtonEventHandler PreviewMouseRightButtonDown;
    event ContextMenuEventHandler ContextMenuOpening;

    FrameworkElement View { get; }
    TextDocument? Document { get; }
    string Text { get; set; }
    string SelectedText { get; set; }
    int SelectionStart { get; }
    int SelectionLength { get; }
    int CaretOffset { get; set; }
    bool IsKeyboardFocusWithin { get; }
    bool WordWrap { get; set; }
    ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
    FontFamily FontFamily { get; set; }
    FontStyle FontStyle { get; set; }
    FontWeight FontWeight { get; set; }
    double FontSize { get; set; }
    Visibility Visibility { get; set; }
    object? SyntaxHighlighting { get; set; }
    ContextMenu? ContextMenu { get; set; }

    void SetDocument(TextDocument document);
    void AddLineTransformer(DocumentColorizingTransformer transformer);
    void AddBackgroundRenderer(IBackgroundRenderer renderer);
    void InsertText(int offset, string value);
    void ReplaceText(int offset, int length, string replacement);
    void Select(int start, int length);
    void SelectAll();
    void ScrollToLine(int lineNumber);
    void Focus();
    void Undo();
    void Redo();
    void Cut();
    void Copy();
    void Paste();
    void Redraw();
    void InvalidateVisual();
    void InvalidateSelectionLayer();
    bool TryGetOffsetFromPoint(Point point, out int offset);
    EditorLineInfo? GetLineByOffset(int offset);
    EditorLineInfo? GetLineByNumber(int lineNumber);
}
