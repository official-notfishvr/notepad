using System.Threading;
using System.Windows;
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
    private readonly SearchHighlightColorizer _searchHighlightColorizer;
    private CancellationTokenSource? _matchCountTokenSource;

    private AppThemeMode _themeMode = AppThemeMode.Dark;
    private bool _isInternalUpdate;
    private bool _statusBarVisible = true;
    private bool _replaceVisible;
    private int _activeTabIndex = -1;

    private FontFamily _editorFontFamily = new("Segoe UI Variable Text");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

    public MainWindow()
    {
        _appSettings = AppSettingsStore.Load();
        InitializeComponent();
        ApplyTheme(GetThemeModeFromSettings(_appSettings.Theme));
        UpdateWindowButtons();
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

        CreateNewTabAndActivate();
        ApplySavedSettings();
        EditorTextBox.Focus();
        Loaded += MainWindow_OnLoaded;
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
}
