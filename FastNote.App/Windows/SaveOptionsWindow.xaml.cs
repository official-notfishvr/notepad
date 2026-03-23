using System.Windows;

namespace FastNote.App;

public partial class SaveOptionsWindow : Window
{
    public string SelectedEncodingKey { get; private set; }
    public string SelectedLineEndingKey { get; private set; }

    public SaveOptionsWindow(IEnumerable<MainWindow.SaveOptionItem> encodings, IEnumerable<MainWindow.SaveOptionItem> lineEndings, string selectedEncodingKey, string selectedLineEndingKey)
    {
        InitializeComponent();
        EncodingComboBox.ItemsSource = encodings.ToList();
        LineEndingComboBox.ItemsSource = lineEndings.ToList();
        EncodingComboBox.SelectedValue = selectedEncodingKey;
        LineEndingComboBox.SelectedValue = selectedLineEndingKey;
        SelectedEncodingKey = selectedEncodingKey;
        SelectedLineEndingKey = selectedLineEndingKey;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedEncodingKey = EncodingComboBox.SelectedValue as string ?? "utf-8";
        SelectedLineEndingKey = LineEndingComboBox.SelectedValue as string ?? "crlf";
        DialogResult = true;
    }
}
