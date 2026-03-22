using FastNote.Core;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow
{
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
