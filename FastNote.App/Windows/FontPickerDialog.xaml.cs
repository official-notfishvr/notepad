using System.Windows;
using System.Windows.Media;

namespace FastNote.App;

public partial class FontPickerDialog : Window
{
    public FontFamily SelectedFontFamily { get; private set; }
    public FontStyle SelectedFontStyle { get; private set; }
    public FontWeight SelectedFontWeight { get; private set; }
    public double SelectedFontSize { get; private set; }

    public FontPickerDialog(FontFamily family, FontStyle style, FontWeight weight, double size)
    {
        InitializeComponent();

        SelectedFontFamily = family;
        SelectedFontStyle = style;
        SelectedFontWeight = weight;
        SelectedFontSize = size;

        foreach (var fontFamily in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            FontList.Items.Add(fontFamily.Source);
        }

        foreach (var styleName in new[] { "Regular", "Italic", "Bold", "Bold Italic" })
        {
            StyleList.Items.Add(styleName);
        }

        foreach (var sizeOption in new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72 })
        {
            SizeList.Items.Add(sizeOption.ToString());
        }

        FontList.SelectedItem = family.Source;
        StyleList.SelectedItem =
            weight == FontWeights.Bold && style == FontStyles.Italic ? "Bold Italic"
            : weight == FontWeights.Bold ? "Bold"
            : style == FontStyles.Italic ? "Italic"
            : "Regular";
        SizeList.SelectedItem = ((int)size).ToString();

        FontList.SelectionChanged += (_, _) => UpdatePreview();
        StyleList.SelectionChanged += (_, _) => UpdatePreview();
        SizeList.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (FontList.SelectedItem is string fontName)
        {
            PreviewText.FontFamily = new FontFamily(fontName);
        }

        if (StyleList.SelectedItem is string styleName)
        {
            PreviewText.FontStyle = styleName.Contains("Italic", StringComparison.Ordinal) ? FontStyles.Italic : FontStyles.Normal;
            PreviewText.FontWeight = styleName.Contains("Bold", StringComparison.Ordinal) ? FontWeights.Bold : FontWeights.Normal;
        }

        if (SizeList.SelectedItem is string sizeName && double.TryParse(sizeName, out var size))
        {
            PreviewText.FontSize = size;
        }
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FontList.SelectedItem is string fontName)
        {
            SelectedFontFamily = new FontFamily(fontName);
        }

        if (StyleList.SelectedItem is string styleName)
        {
            SelectedFontStyle = styleName.Contains("Italic", StringComparison.Ordinal) ? FontStyles.Italic : FontStyles.Normal;
            SelectedFontWeight = styleName.Contains("Bold", StringComparison.Ordinal) ? FontWeights.Bold : FontWeights.Normal;
        }

        if (SizeList.SelectedItem is string sizeName && double.TryParse(sizeName, out var size))
        {
            SelectedFontSize = size;
        }

        DialogResult = true;
    }
}
