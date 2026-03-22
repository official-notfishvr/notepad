using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int WordWrapSoftLimitCharacters = 2_000_000;
    private const long LargeFileThresholdBytes = 8L * 1024 * 1024;
    private const int LoadReadBufferCharacters = 256 * 1024;
    private const int UiAppendBatchCharacters = 512 * 1024;
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
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _statusRefreshTimer.Tick += (_, _) => RefreshActiveTabUi();
        _statusRefreshTimer.Start();
        PreviewViewport.TopLineChanged += PreviewViewport_OnTopLineChanged;

        CreateNewTabAndActivate();
        EditorTextBox.Focus();
    }
}
