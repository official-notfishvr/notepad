using System.Windows;

namespace FastNote.App;

public partial class UnsavedChangesDialog : Window
{
    public MainWindow.UnsavedChangesChoice Choice { get; private set; } = MainWindow.UnsavedChangesChoice.Cancel;

    public UnsavedChangesDialog(string fileName)
    {
        InitializeComponent();
        FileNameText.Text = fileName;
        MessageText.Text = "If you don't save, your changes will be lost.";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = MainWindow.UnsavedChangesChoice.Save;
        DialogResult = true;
    }

    private void DontSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = MainWindow.UnsavedChangesChoice.DontSave;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = MainWindow.UnsavedChangesChoice.Cancel;
        DialogResult = false;
    }
}
