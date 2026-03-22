using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int WordWrapSoftLimitCharacters = 2_000_000;
    private const int TabRetentionLimit = 12;

    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loadTokens = [];
    private readonly DispatcherTimer _statusRefreshTimer;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private int _activeTabIndex = -1;

    private FontFamily _editorFontFamily = new("Consolas");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(AppThemeMode.Dark);
        UpdateWindowButtons();

        _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _statusRefreshTimer.Tick += (_, _) =>
        {
            var tab = GetActiveTab();
            if (tab is null || tab.IsLoading)
            {
                return;
            }

            RefreshActiveTabUi();
        };
        _statusRefreshTimer.Start();

        CreateNewTabAndActivate();
        EditorTextBox.Focus();
    }
}