using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FastNote.App.Settings;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int TabRetentionLimit = 12;

    private readonly AppSettings _appSettings;
    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loadTokens = [];
    private readonly DispatcherTimer _statusRefreshTimer;
    private readonly DispatcherTimer _findRefreshTimer;
    private readonly DispatcherTimer _sessionSaveTimer;
    private readonly SyntaxHighlightColorizer _syntaxHighlightColorizer;
    private readonly SearchHighlightColorizer _searchHighlightColorizer;
    private readonly List<string> _recentFiles;
    private CancellationTokenSource? _matchCountTokenSource;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private bool _findOptionsVisible = true;
    private bool _skipCloseConfirmation;
    private int _activeTabIndex = -1;
    private Point _tabDragStartPoint;
    private Guid? _pendingTabDragId;
    private Guid? _draggingTabId;

    private FontFamily _editorFontFamily = new("Segoe UI Variable Text");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

    public MainWindow()
    {
        _appSettings = AppSettingsStore.Load();
        _recentFiles = _appSettings.RecentFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        InitializeComponent();
        ApplyHybridShellLayout();
        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        UpdateWindowButtons();
        TabStripScrollViewer.SizeChanged += (_, _) => RenderTabs();
        TabStripScrollViewer.ScrollChanged += (_, _) => UpdateTabStripNavigationState();
        WireTabChromeHover(TabScrollLeftButton, TabScrollLeftSurface);
        WireTabChromeHover(TabScrollRightButton, TabScrollRightSurface);
        EditorTextBox.TextChanged += EditorTextBox_OnTextChanged;
        EditorTextBox.TextArea.SelectionChanged += EditorTextBox_OnSelectionChanged;
        EditorTextBox.TextArea.Caret.PositionChanged += (_, _) => EditorTextBox_OnSelectionChanged(EditorTextBox, EventArgs.Empty);
        EditorTextBox.Document = new TextDocument();
        EditorTextBox.Options.EnableHyperlinks = false;
        EditorTextBox.Options.EnableEmailHyperlinks = false;
        EditorTextBox.Options.EnableRectangularSelection = false;
        EditorTextBox.Options.EnableTextDragDrop = false;
        EditorTextBox.Options.HighlightCurrentLine = false;
        EditorTextBox.Options.AllowScrollBelowDocument = false;
        _syntaxHighlightColorizer = new SyntaxHighlightColorizer();
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_syntaxHighlightColorizer);
        _searchHighlightColorizer = new SearchHighlightColorizer();
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_searchHighlightColorizer);

        _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
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
        _findRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _findRefreshTimer.Tick += (_, _) =>
        {
            _findRefreshTimer.Stop();
            ApplyHighlightAllState();
            BeginMatchCountUpdate();
        };
        _sessionSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _sessionSaveTimer.Tick += (_, _) => SaveSessionSnapshot();

        ApplySavedSettings();
        if (!TryRestorePreviousSession())
        {
            CreateNewTabAndActivate();
        }

        RebuildRecentFilesMenu();
        EditorTextBox.Focus();
        Loaded += MainWindow_OnLoaded;
        _sessionSaveTimer.Start();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_appSettings.SetupCompleted)
        {
            return;
        }

        if (FileAssociationInstaller.IsTxtAssociationInstalledForCurrentApp())
        {
            _appSettings.SetupCompleted = true;
            AppSettingsStore.Save(_appSettings);
            return;
        }

        ShowSettingsWindow();
        _appSettings.SetupCompleted = true;
        SaveSettings();
    }

    private void WireTabChromeHover(System.Windows.Controls.Button button, Border surface)
    {
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled)
            {
                surface.Background = (Brush)FindResource("TabHoverBrush");
            }
        };
        button.MouseLeave += (_, _) => surface.Background = (Brush)FindResource("TabInactiveBrush");
    }
}
