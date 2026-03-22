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
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public static readonly DependencyProperty ShowLineNumbersProperty =
        DependencyProperty.Register(
            nameof(ShowLineNumbers),
            typeof(bool),
            typeof(DocumentViewport),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Typeface _typeface =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FileDocument? _document;
    private long _topLine;
    private double _lineHeight = 20;
    private double _scrollAccumulator;

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

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public FileDocument? Document
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
            _scrollAccumulator = 0;

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
            DrawCenteredMessage(drawingContext, "Open or drop a text file to get started");
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = GetBrush("EditorForegroundBrush", new SolidColorBrush(Color.FromRgb(255, 255, 255)));
        var mutedBrush = GetBrush("EditorMutedBrush", new SolidColorBrush(Color.FromRgb(110, 110, 110)));
        var sample = CreateFormattedText("Hg", pixelsPerDip, textBrush);
        _lineHeight = sample.Height + 5;

        var lineNumberWidth = ShowLineNumbers ? MeasureLineNumberWidth(pixelsPerDip, mutedBrush) + 12 : 0;
        var paddingX = 14d + lineNumberWidth;
        var lineWidth = Math.Max(80, ActualWidth - paddingX - 14);
        var lines = _document.ReadLines(_topLine, VisibleLineCount + 4);

        for (var i = 0; i < lines.Count; i++)
        {
            var y = i * _lineHeight + 8;
            if (y > ActualHeight + _lineHeight)
            {
                break;
            }

            if (ShowLineNumbers)
            {
                var lineNum = CreateFormattedText(
                    (_topLine + i + 1).ToString(),
                    pixelsPerDip,
                    mutedBrush);
                lineNum.TextAlignment = TextAlignment.Right;
                lineNum.MaxTextWidth = lineNumberWidth - 8;
                drawingContext.DrawText(lineNum, new Point(6, y));
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
            DrawLoadingHint(drawingContext, pixelsPerDip, mutedBrush);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        _scrollAccumulator -= e.Delta / 120.0 * 3;
        var linesToScroll = (int)_scrollAccumulator;
        if (linesToScroll != 0)
        {
            _scrollAccumulator -= linesToScroll;
            ScrollLines(linesToScroll);
        }

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

    private double MeasureLineNumberWidth(double pixelsPerDip, Brush brush)
    {
        var totalLines = _document?.EstimateTotalLineCount() ?? 1;
        var digits = Math.Max(4, totalLines.ToString().Length);
        var sample = CreateFormattedText(new string('9', digits), pixelsPerDip, brush);
        return sample.Width;
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
        var mutedBrush = GetBrush("EditorMutedBrush", new SolidColorBrush(Color.FromRgb(110, 110, 110)));
        var text = CreateFormattedText(message, pixelsPerDip, mutedBrush);
        drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }

    private void DrawLoadingHint(DrawingContext drawingContext, double pixelsPerDip, Brush mutedBrush)
    {
        var hint = CreateFormattedText(
            "Indexing…",
            pixelsPerDip,
            mutedBrush);
        drawingContext.DrawText(hint, new Point(Math.Max(12, ActualWidth - hint.Width - 18), Math.Max(10, ActualHeight - hint.Height - 12)));
    }

    private static Brush GetBrush(string key, Brush fallback)
    {
        return Application.Current.Resources[key] as Brush ?? fallback;
    }
}