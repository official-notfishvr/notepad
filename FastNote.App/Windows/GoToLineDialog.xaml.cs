using System.Windows;

namespace FastNote.App;

public partial class GoToLineDialog : Window
{
    public int LineNumber { get; private set; }

    public GoToLineDialog(int currentLine)
    {
        InitializeComponent();
        LineBox.Text = currentLine.ToString();
        Loaded += (_, _) =>
        {
            LineBox.Focus();
            LineBox.SelectAll();
        };
    }

    private void GoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LineBox.Text, out var lineNumber))
        {
            return;
        }

        LineNumber = lineNumber;
        DialogResult = true;
    }
}
