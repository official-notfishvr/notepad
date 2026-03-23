using FastNote.App.Settings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastNote.App;

public partial class SettingsWindow : Window
{
    public string SelectedTheme { get; private set; } = "Dark";
    public string SelectedAppearanceMode { get; private set; } = "Classic";
    public bool ShowStatusBar { get; private set; }
    public bool DefaultWordWrap { get; private set; }
    public bool RestorePreviousSession { get; private set; }

    public SettingsWindow(AppSettings settings, bool isTxtAssociated)
    {
        InitializeComponent();

        AppearanceComboBox.SelectedIndex = string.Equals(settings.AppearanceMode, "Windows11", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ThemeComboBox.SelectedIndex = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        StatusBarCheckBox.IsChecked = settings.StatusBarVisible;
        WordWrapCheckBox.IsChecked = settings.DefaultWordWrap;
        RestoreSessionCheckBox.IsChecked = settings.RestorePreviousSession;
        RefreshAssociationState(isTxtAssociated);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedAppearanceMode = ((AppearanceComboBox.SelectedItem as ComboBoxItem)?.Content as string) == "Windows 11" ? "Windows11" : "Classic";
        SelectedTheme = ((ThemeComboBox.SelectedItem as ComboBoxItem)?.Content as string) ?? "Dark";
        ShowStatusBar = StatusBarCheckBox.IsChecked == true;
        DefaultWordWrap = WordWrapCheckBox.IsChecked == true;
        RestorePreviousSession = RestoreSessionCheckBox.IsChecked == true;
        DialogResult = true;
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
}
