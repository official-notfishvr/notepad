using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App.Editor;

public sealed class AvalonEditSurface : IEditorSurface
{
    private readonly TextEditor _editor;

    public AvalonEditSurface(Panel host)
    {
        _editor = CreateEditor();
        host.Children.Add(_editor);
    }

    public event EventHandler? TextChanged
    {
        add => _editor.TextChanged += value;
        remove => _editor.TextChanged -= value;
    }

    public event EventHandler? SelectionChanged
    {
        add => _editor.TextArea.SelectionChanged += value;
        remove => _editor.TextArea.SelectionChanged -= value;
    }

    public event EventHandler? CaretPositionChanged
    {
        add => _editor.TextArea.Caret.PositionChanged += value;
        remove => _editor.TextArea.Caret.PositionChanged -= value;
    }

    public event MouseButtonEventHandler PreviewMouseRightButtonDown
    {
        add => _editor.PreviewMouseRightButtonDown += value;
        remove => _editor.PreviewMouseRightButtonDown -= value;
    }

    public event ContextMenuEventHandler ContextMenuOpening
    {
        add => _editor.ContextMenuOpening += value;
        remove => _editor.ContextMenuOpening -= value;
    }

    public FrameworkElement View => _editor;
    public TextDocument? Document => _editor.Document;
    public string Text
    {
        get => _editor.Text;
        set => _editor.Text = value;
    }
    public string SelectedText
    {
        get => _editor.SelectedText;
        set => _editor.SelectedText = value;
    }
    public int SelectionStart => _editor.SelectionStart;
    public int SelectionLength => _editor.SelectionLength;
    public int CaretOffset
    {
        get => _editor.CaretOffset;
        set => _editor.CaretOffset = value;
    }
    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }
    public bool IsKeyboardFocusWithin => _editor.IsKeyboardFocusWithin;
    public bool WordWrap
    {
        get => _editor.WordWrap;
        set => _editor.WordWrap = value;
    }
    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => _editor.HorizontalScrollBarVisibility;
        set => _editor.HorizontalScrollBarVisibility = value;
    }
    public FontFamily FontFamily
    {
        get => _editor.FontFamily;
        set => _editor.FontFamily = value;
    }
    public FontStyle FontStyle
    {
        get => _editor.FontStyle;
        set => _editor.FontStyle = value;
    }
    public FontWeight FontWeight
    {
        get => _editor.FontWeight;
        set => _editor.FontWeight = value;
    }
    public double FontSize
    {
        get => _editor.FontSize;
        set => _editor.FontSize = value;
    }
    public Visibility Visibility
    {
        get => _editor.Visibility;
        set => _editor.Visibility = value;
    }
    public object? SyntaxHighlighting
    {
        get => _editor.SyntaxHighlighting;
        set => _editor.SyntaxHighlighting = value as ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition;
    }
    public ContextMenu? ContextMenu
    {
        get => _editor.ContextMenu;
        set => _editor.ContextMenu = value;
    }

    public void SetDocument(TextDocument document) => _editor.Document = document;

    public void AddLineTransformer(DocumentColorizingTransformer transformer) => _editor.TextArea.TextView.LineTransformers.Add(transformer);

    public void AddBackgroundRenderer(IBackgroundRenderer renderer) => _editor.TextArea.TextView.BackgroundRenderers.Add(renderer);

    public void InsertText(int offset, string value) => _editor.Document.Insert(offset, value);

    public void ReplaceText(int offset, int length, string replacement) => _editor.Document.Replace(offset, length, replacement);

    public void Select(int start, int length) => _editor.Select(start, length);

    public void SelectAll() => _editor.SelectAll();

    public void ScrollToLine(int lineNumber) => _editor.ScrollToLine(lineNumber);

    public void Focus() => _editor.Focus();

    public void Undo() => _editor.Undo();

    public void Redo() => _editor.Redo();

    public void Cut() => _editor.Cut();

    public void Copy() => _editor.Copy();

    public void Paste() => _editor.Paste();

    public void Redraw() => _editor.TextArea.TextView.Redraw();

    public void InvalidateVisual() => _editor.TextArea.TextView.InvalidateVisual();

    public void InvalidateSelectionLayer() => _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    public bool TryGetOffsetFromPoint(Point point, out int offset)
    {
        offset = 0;
        var position = _editor.GetPositionFromPoint(point);
        if (!position.HasValue || _editor.Document is null)
        {
            return false;
        }

        offset = _editor.Document.GetOffset(position.Value.Location);
        return true;
    }

    public EditorLineInfo? GetLineByOffset(int offset)
    {
        var document = _editor.Document;
        if (document is null)
        {
            return null;
        }

        var safeOffset = document.TextLength == 0 ? 0 : Math.Clamp(offset, 0, document.TextLength);
        var line = document.GetLineByOffset(safeOffset);
        return new EditorLineInfo(line.LineNumber, line.Offset, line.Length, line.TotalLength);
    }

    public EditorLineInfo? GetLineByNumber(int lineNumber)
    {
        var document = _editor.Document;
        if (document is null || lineNumber <= 0 || lineNumber > document.LineCount)
        {
            return null;
        }

        var line = document.GetLineByNumber(lineNumber);
        return new EditorLineInfo(line.LineNumber, line.Offset, line.Length, line.TotalLength);
    }

    private static TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            AllowDrop = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 10, 4, 10),
            ShowLineNumbers = false,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        editor.SetResourceReference(Control.BackgroundProperty, "EditorBackgroundBrush");
        editor.SetResourceReference(Control.ForegroundProperty, "EditorForegroundBrush");
        editor.SetResourceReference(Control.FontFamilyProperty, "EditorFont");
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.EnableRectangularSelection = false;
        editor.Options.EnableTextDragDrop = false;
        editor.Options.HighlightCurrentLine = false;
        editor.Options.AllowScrollBelowDocument = false;
        return editor;
    }
}
