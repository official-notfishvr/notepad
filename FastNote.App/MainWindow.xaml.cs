using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App;

public partial class MainWindow : Window
{
    private const double DefaultEditorFontSize = 14;
    private const int TabRetentionLimit = 12;

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

    private FontFamily _editorFontFamily = new("Consolas");
    private FontStyle _editorFontStyle = FontStyles.Normal;
    private FontWeight _editorFontWeight = FontWeights.Normal;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(AppThemeMode.Dark);
        UpdateWindowButtons();
        DocumentViewportControl.TopLineChanged += (_, _) => UpdateStatusBar();
        DocumentViewportControl.EditorFontSize = DefaultEditorFontSize;
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
        EditorTextBox.Focus();
    }
}
