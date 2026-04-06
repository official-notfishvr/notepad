using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastNote.App.Editor;
using FastNote.App.Settings;
using ICSharpCode.AvalonEdit.Document;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int TabRetentionLimit = 12;

    private readonly AppSettings _appSettings;
    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _loadTokens = [];
    private readonly DispatcherTimer _findRefreshTimer;
    private readonly DispatcherTimer _sessionSaveTimer;
    private readonly IEditorSurface _editor;
    private readonly SyntaxHighlightColorizer _syntaxHighlightColorizer;
    private readonly SearchHighlightColorizer _searchHighlightColorizer;
    private readonly SpellCheckColorizer _spellCheckColorizer;
    private readonly List<string> _recentFiles;
    private readonly bool _restorePreviousSessionOnStartup;
    private readonly bool _autoShowSetupOnFirstLaunch;
    private CancellationTokenSource? _matchCountTokenSource;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private bool _findOptionsVisible = true;
    private bool _sessionSnapshotPending = true;
    private bool _skipCloseConfirmation;
    private int _activeTabIndex = -1;
    private Point _tabDragStartPoint;
    private Guid? _pendingTabDragId;
    private Guid? _draggingTabId;

    private FontFamily _editorFontFamily = new("Segoe UI Variable Text");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

    public MainWindow(bool restorePreviousSessionOnStartup = true, bool autoShowSetupOnFirstLaunch = true)
    {
        _restorePreviousSessionOnStartup = restorePreviousSessionOnStartup;
        _autoShowSetupOnFirstLaunch = autoShowSetupOnFirstLaunch;
        _appSettings = AppSettingsStore.Load();
        _recentFiles = _appSettings.RecentFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        InitializeComponent();
        _editor = new AvalonEditSurface(EditorHost);
        ApplyHybridShellLayout();
        UpdateWindowButtons();
        TabStripScrollViewer.SizeChanged += (_, _) => RenderTabs();
        TabStripScrollViewer.ScrollChanged += (_, _) => UpdateTabStripNavigationState();
        WireTabChromeHover(TabScrollLeftButton, TabScrollLeftSurface);
        WireTabChromeHover(TabScrollRightButton, TabScrollRightSurface);
        _editor.TextChanged += EditorTextBox_OnTextChanged;
        _editor.SelectionChanged += EditorTextBox_OnSelectionChanged;
        _editor.CaretPositionChanged += (_, _) => EditorTextBox_OnSelectionChanged(_editor, EventArgs.Empty);
        _editor.SetDocument(new TextDocument());
        _syntaxHighlightColorizer = new SyntaxHighlightColorizer();
        _editor.AddLineTransformer(_syntaxHighlightColorizer);
        _searchHighlightColorizer = new SearchHighlightColorizer();
        _editor.AddLineTransformer(_searchHighlightColorizer);
        _spellCheckColorizer = new SpellCheckColorizer();
        _editor.AddBackgroundRenderer(_spellCheckColorizer);
        _editor.PreviewMouseRightButtonDown += EditorTextBox_OnPreviewMouseRightButtonDown;
        _editor.ContextMenuOpening += EditorTextBox_OnContextMenuOpening;

        _findRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _findRefreshTimer.Tick += (_, _) =>
        {
            _findRefreshTimer.Stop();
            ApplyHighlightAllState();
            BeginMatchCountUpdate();
        };
        _sessionSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _sessionSaveTimer.Tick += (_, _) => FlushPendingSessionSnapshot();

        ApplySavedSettings();
        if (!_restorePreviousSessionOnStartup || !TryRestorePreviousSession())
        {
            CreateNewTabAndActivate(saveSession: false);
        }

        Dispatcher.BeginInvoke(RebuildRecentFilesMenu, DispatcherPriority.Background);
        _editor.Focus();
        Loaded += MainWindow_OnLoaded;
        _sessionSaveTimer.Start();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_autoShowSetupOnFirstLaunch)
        {
            return;
        }

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
