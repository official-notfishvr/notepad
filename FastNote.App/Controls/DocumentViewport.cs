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

    public static readonly DependencyProperty EditorFontSizeProperty =
        DependencyProperty.Register(
            nameof(EditorFontSize),
            typeof(double),
            typeof(DocumentViewport),
            new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WrapTextProperty =
        DependencyProperty.Register(
            nameof(WrapText),
            typeof(bool),
            typeof(DocumentViewport),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Typeface _typeface =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private LargeFileDocument? _document;
    private long _topLine;
    private double _lineHeight = 20;

    public event EventHandler? TopLineChanged;

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public bool WrapText
    {
        get => (bool)GetValue(WrapTextProperty);
        set => SetValue(WrapTextProperty, value);
    }

    public LargeFileDocument? Document
    {
        get => _document;
        set
        {
            if (_document is not null)
            {
                _document.ProgressChanged -= DocumentOnProgressChanged;
            }

            _document = value;
            _topLine = 0;

            if (_document is not null)
            {
                _document.ProgressChanged += DocumentOnProgressChanged;
            }

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
            DrawCenteredMessage(drawingContext, "Open or drop a text file");
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = GetBrush("EditorForegroundBrush", new SolidColorBrush(Color.FromRgb(36, 39, 45)));
        var sample = CreateFormattedText("Hg", pixelsPerDip, textBrush);
        _lineHeight = sample.Height + 4;

        var paddingX = 10d;
        var lineWidth = Math.Max(80, ActualWidth - paddingX * 2);
        var lines = _document.ReadLines(_topLine, VisibleLineCount + 2);

        for (var i = 0; i < lines.Count; i++)
        {
            var y = i * _lineHeight + 6;
            if (y > ActualHeight)
            {
                break;
            }

            var contentText = CreateFormattedText(lines[i], pixelsPerDip, textBrush);
            if (WrapText)
            {
                contentText.MaxTextWidth = lineWidth;
            }

            drawingContext.DrawText(contentText, new Point(paddingX, y));
        }

        if (!_document.IsIndexComplete)
        {
            DrawLoadingHint(drawingContext, pixelsPerDip);
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
                ScrollToLine(Math.Max(0, _document.EstimateTotalLineCount() - VisibleLineCount));
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

        var maxTopLine = Math.Max(0, _document.EstimateTotalLineCount() - VisibleLineCount);
        var clamped = Math.Clamp(line, 0, maxTopLine);
        if (clamped == _topLine)
        {
            return;
        }

        _topLine = clamped;
        InvalidateVisual();
        TopLineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DocumentOnProgressChanged(object? sender, FileLoadProgress e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InvalidateVisual();
            TopLineChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private FormattedText CreateFormattedText(string text, double pixelsPerDip, Brush foreground)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            EditorFontSize,
            foreground,
            pixelsPerDip);
    }

    private void DrawCenteredMessage(DrawingContext drawingContext, string message)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = CreateFormattedText(message, pixelsPerDip, GetBrush("EditorMutedBrush", new SolidColorBrush(Color.FromRgb(110, 113, 120))));
        drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }

    private void DrawLoadingHint(DrawingContext drawingContext, double pixelsPerDip)
    {
        var hint = CreateFormattedText(
            "Loading more lines...",
            pixelsPerDip,
            GetBrush("EditorMutedBrush", new SolidColorBrush(Color.FromRgb(110, 113, 120))));
        drawingContext.DrawText(hint, new Point(Math.Max(12, ActualWidth - hint.Width - 18), Math.Max(10, ActualHeight - hint.Height - 10)));
    }

    private static Brush GetBrush(string key, Brush fallback)
    {
        return Application.Current.Resources[key] as Brush ?? fallback;
    }
}
