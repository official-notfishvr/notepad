using FastNote.App.Settings;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class SettingsWindow : Window
{
    public string SelectedTheme { get; private set; } = "Dark";
    public bool ShowStatusBar { get; private set; }
    public bool DefaultWordWrap { get; private set; }
    public bool RestorePreviousSession { get; private set; }
    public FontFamily SelectedFontFamily { get; private set; }
    public FontStyle SelectedFontStyle { get; private set; }
    public FontWeight SelectedFontWeight { get; private set; }
    public double SelectedFontSize { get; private set; }

    public SettingsWindow(AppSettings settings, bool isTxtAssociated)
    {
        InitializeComponent();

        ThemeComboBox.SelectedIndex = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        StatusBarCheckBox.IsChecked = settings.StatusBarVisible;
        WordWrapCheckBox.IsChecked = settings.DefaultWordWrap;
        RestoreSessionCheckBox.IsChecked = settings.RestorePreviousSession;
        SelectedFontFamily = new FontFamily(string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Segoe UI Variable Text" : settings.EditorFontFamily);
        SelectedFontStyle = string.Equals(settings.EditorFontStyle, "Italic", StringComparison.OrdinalIgnoreCase) ? FontStyles.Italic : FontStyles.Normal;
        SelectedFontWeight = string.Equals(settings.EditorFontWeight, "Bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal;
        SelectedFontSize = settings.EditorFontSize <= 0 ? 14 : settings.EditorFontSize;
        UpdateFontSummary();
        RefreshAssociationState(isTxtAssociated);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedTheme = ThemeComboBox.SelectedIndex == 1 ? "Light" : "Dark";
        ShowStatusBar = StatusBarCheckBox.IsChecked == true;
        DefaultWordWrap = WordWrapCheckBox.IsChecked == true;
        RestorePreviousSession = RestoreSessionCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void ChooseFontButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FontPickerDialog(SelectedFontFamily, SelectedFontStyle, SelectedFontWeight, SelectedFontSize) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFontFamily = dialog.SelectedFontFamily;
        SelectedFontStyle = dialog.SelectedFontStyle;
        SelectedFontWeight = dialog.SelectedFontWeight;
        SelectedFontSize = dialog.SelectedFontSize;
        UpdateFontSummary();
    }

    private void InstallAssociationButton_OnClick(object sender, RoutedEventArgs e)
    {
        FileAssociationInstaller.InstallTxtAssociationForCurrentUser();
        RefreshAssociationState(true);
        MessageBox.Show(this, "FastNote is now set to open .txt files when you double-click them.", "FastNote", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshAssociationState(bool isTxtAssociated)
    {
        AssociationDescriptionText.Text = isTxtAssociated
            ? "Double-clicking supported text and code files already opens in FastNote."
            : "Supported text and code files are still opening in another app.";
        AssociationPathText.Text = FileAssociationInstaller.GetIconPath();
        AssociationExtensionsText.Text = $"Extensions: {FileAssociationInstaller.SupportedExtensionsLabel}";
        InstallAssociationButton.Content = isTxtAssociated ? "Reinstall" : "Set as default";
    }

    private void HeaderRegion_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
    }

    private void UpdateFontSummary()
    {
        var styleLabel =
            SelectedFontWeight == FontWeights.Bold && SelectedFontStyle == FontStyles.Italic ? "Bold Italic"
            : SelectedFontWeight == FontWeights.Bold ? "Bold"
            : SelectedFontStyle == FontStyles.Italic ? "Italic"
            : "Regular";
        FontSummaryText.Text = $"{SelectedFontFamily.Source}, {SelectedFontSize:N0} pt, {styleLabel}";
    }
}
