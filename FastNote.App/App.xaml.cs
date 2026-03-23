using System.Windows;
using System.Text;

namespace FastNote.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            _ = mainWindow.OpenFileAsync(e.Args[0]);
        }
    }
}
