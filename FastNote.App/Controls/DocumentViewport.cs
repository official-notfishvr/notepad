using FastNote.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App.Controls;

public sealed class DocumentViewport : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(DocumentViewport),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private LargeFileDocument? _document;
    private long _topLine;
    private double _lineHeight = 20;

    public event EventHandler? TopLineChanged;

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public LargeFileDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _topLine = 0;
            InvalidateMeasure();
            InvalidateVisual();
            TopLineChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public long TopLine => _topLine;

    public int VisibleLineCount => Math.Max(1, (int)Math.Floor(Math.Max(ActualHeight, _lineHeight) / _lineHeight));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_document is null)
        {
            DrawCenteredMessage(drawingContext, "Drop a file here or press Ctrl+O");
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var sample = CreateFormattedText("Hg", pixelsPerDip, Brushes.Black);
        _lineHeight = sample.Height + 3;

        var gutterWidth = CalculateGutterWidth(pixelsPerDip);
        var gutterBrush = new SolidColorBrush(Color.FromRgb(246, 248, 252));
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(223, 229, 238)), 1);
        var lineNumberBrush = new SolidColorBrush(Color.FromRgb(115, 127, 145));
        var contentBrush = new SolidColorBrush(Color.FromRgb(28, 39, 55));

        drawingContext.DrawRectangle(gutterBrush, null, new Rect(0, 0, gutterWidth, ActualHeight));
        drawingContext.DrawLine(borderPen, new Point(gutterWidth, 0), new Point(gutterWidth, ActualHeight));

        var lines = _document.ReadLines(_topLine, VisibleLineCount);
        for (var i = 0; i < lines.Count; i++)
        {
            var lineNumber = _topLine + i + 1;
            var y = i * _lineHeight + 6;

            var lineNumberText = CreateFormattedText(lineNumber.ToString("N0", CultureInfo.InvariantCulture), pixelsPerDip, lineNumberBrush);
            drawingContext.DrawText(lineNumberText, new Point(gutterWidth - lineNumberText.Width - 12, y));

            var contentText = CreateFormattedText(lines[i], pixelsPerDip, contentBrush);
            contentText.MaxTextWidth = Math.Max(80, ActualWidth - gutterWidth - 24);
            drawingContext.DrawText(contentText, new Point(gutterWidth + 12, y));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        ScrollLines(e.Delta > 0 ? -3 : 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                ScrollLines(-1);
                e.Handled = true;
                break;
            case Key.Down:
                ScrollLines(1);
                e.Handled = true;
                break;
            case Key.PageUp:
                ScrollLines(-Math.Max(1, VisibleLineCount - 1));
                e.Handled = true;
                break;
            case Key.PageDown:
                ScrollLines(Math.Max(1, VisibleLineCount - 1));
                e.Handled = true;
                break;
            case Key.Home:
                ScrollToLine(0);
                e.Handled = true;
                break;
            case Key.End:
                ScrollToLine(Math.Max(0, _document.LineCount - VisibleLineCount));
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    public void ScrollLines(int delta)
    {
        ScrollToLine(_topLine + delta);
    }

    public void ScrollToLine(long line)
    {
        if (_document is null)
        {
            return;
        }

        var maxTopLine = Math.Max(0, _document.LineCount - VisibleLineCount);
        var clamped = Math.Clamp(line, 0, maxTopLine);
        if (clamped == _topLine)
        {
            return;
        }

        _topLine = clamped;
        InvalidateVisual();
        TopLineChanged?.Invoke(this, EventArgs.Empty);
    }

    private double CalculateGutterWidth(double pixelsPerDip)
    {
        var maxLineNumber = _document?.LineCount ?? 1;
        var text = CreateFormattedText(maxLineNumber.ToString("N0", CultureInfo.InvariantCulture), pixelsPerDip, Brushes.Black);
        return text.Width + 28;
    }

    private FormattedText CreateFormattedText(string text, double pixelsPerDip, Brush foreground)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            13,
            foreground,
            pixelsPerDip);
    }

    private void DrawCenteredMessage(DrawingContext drawingContext, string message)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = CreateFormattedText(message, pixelsPerDip, new SolidColorBrush(Color.FromRgb(95, 109, 131)));
        drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }
}
