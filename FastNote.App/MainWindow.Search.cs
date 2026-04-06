using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow
{
    private int GetEditorDocumentLength()
    {
        return _editor.Document?.TextLength ?? 0;
    }

    private void FindNextInternal()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var text = _editor.Text;
        var start = _editor.SelectionStart + _editor.SelectionLength;

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            var match = FindRegexMatch(text, query, start, forward: true);
            index = match?.Index ?? -1;
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: true, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            var length = UseRegexCheckBox.IsChecked == true ? FindRegexMatch(text, query, start, forward: true)?.Length ?? query.Length : query.Length;
            ScrollToMatchAndSelect(index, length);
        }
        else
        {
            FlashNotFound();
        }

        RefreshHighlightAll();
    }

    private void FindPreviousInternal()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var text = _editor.Text;
        var start = Math.Max(0, _editor.SelectionStart - 1);

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            var match = FindRegexMatch(text, query, start, forward: false);
            index = match?.Index ?? -1;
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: false, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            var length = UseRegexCheckBox.IsChecked == true ? FindRegexMatch(text, query, start, forward: false)?.Length ?? query.Length : query.Length;
            ScrollToMatchAndSelect(index, length);
        }
        else
        {
            FlashNotFound();
        }

        RefreshHighlightAll();
    }

    private void ScrollToMatchAndSelect(int index, int length)
    {
        _editor.Select(index, length);
        var documentLength = GetEditorDocumentLength();
        var line = _editor.GetLineByOffset(documentLength == 0 ? 0 : Math.Clamp(index, 0, documentLength));
        if (line is { } lineInfo)
        {
            _editor.ScrollToLine(lineInfo.LineNumber);
        }

        ApplyFindHighlight();
        UpdateMatchCountLabel();
    }

    private void FlashNotFound()
    {
        FindTextBox.Background = GetResourceBrush("FindNotFoundBrush", Color.FromArgb(0x55, 0xC4, 0x2B, 0x1C));
        FindTextBox.Dispatcher.BeginInvoke(
            () =>
            {
                FindTextBox.Background = GetResourceBrush("InputBackgroundBrush", Color.FromArgb(0xFF, 0x33, 0x33, 0x33));
            },
            System.Windows.Threading.DispatcherPriority.Background
        );
        ResetFindHighlight();
        UpdateMatchCountLabel();
    }

    private static int FindWithOptions(string text, string query, int start, bool forward, StringComparison comparison, bool wholeWord)
    {
        if (forward)
        {
            var searchStart = Math.Max(0, start);
            while (true)
            {
                var index = text.IndexOf(query, searchStart, comparison);
                if (index < 0 && searchStart > 0)
                {
                    searchStart = 0;
                    continue;
                }

                if (index < 0)
                {
                    return -1;
                }

                if (!wholeWord || IsWholeWordMatch(text, index, query.Length))
                {
                    return index;
                }

                searchStart = index + Math.Max(1, query.Length);
            }
        }

        var reverseStart = Math.Clamp(start, 0, Math.Max(0, text.Length - 1));
        while (true)
        {
            var index = reverseStart > 0 ? text.LastIndexOf(query, reverseStart, comparison) : text.LastIndexOf(query, comparison);
            if (index < 0 && reverseStart < text.Length - 1)
            {
                reverseStart = text.Length - 1;
                continue;
            }
            if (index < 0)
            {
                return -1;
            }

            if (!wholeWord || IsWholeWordMatch(text, index, query.Length))
            {
                return index;
            }

            reverseStart = index - 1;
            if (reverseStart < 0)
            {
                return -1;
            }
        }
    }

    private static Match? FindRegexMatch(string text, string pattern, int start, bool forward)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.Multiline);
            if (forward)
            {
                var match = regex.Match(text, start);
                if (!match.Success)
                {
                    match = regex.Match(text);
                }

                return match.Success ? match : null;
            }

            var matches = regex.Matches(text[..start]);
            return matches.Count > 0 ? matches[^1] : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWholeWordMatch(string text, int index, int length)
    {
        var leftBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]) && text[index - 1] != '_';
        var rightIndex = index + length;
        var rightBoundary = rightIndex >= text.Length || !char.IsLetterOrDigit(text[rightIndex]) && text[rightIndex] != '_';
        return leftBoundary && rightBoundary;
    }

    private Match? GetCurrentRegexMatch(string text, string pattern, int selectionStart, int selectionLength)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.Multiline | (MatchCaseCheckBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase));
            return regex.Matches(text).Cast<Match>().FirstOrDefault(match => match.Index == selectionStart && match.Length == selectionLength);
        }
        catch
        {
            return null;
        }
    }

    private bool IsCurrentSelectionSearchMatch(string sourceText, string query)
    {
        if (_editor.SelectionLength <= 0)
        {
            return false;
        }

        if (UseRegexCheckBox.IsChecked == true)
        {
            return GetCurrentRegexMatch(sourceText, query, _editor.SelectionStart, _editor.SelectionLength) is not null;
        }

        var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.Equals(_editor.SelectedText, query, comparison))
        {
            return false;
        }

        return WholeWordCheckBox.IsChecked != true || IsWholeWordMatch(sourceText, _editor.SelectionStart, _editor.SelectionLength);
    }

    private bool TryReplaceCurrentMatch()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        var sourceText = _editor.Text;
        if (!IsCurrentSelectionSearchMatch(sourceText, query))
        {
            return false;
        }

        if (UseRegexCheckBox.IsChecked == true)
        {
            var match = GetCurrentRegexMatch(sourceText, query, _editor.SelectionStart, _editor.SelectionLength);
            if (match is null)
            {
                return false;
            }

            var replacement = match.Result(ReplaceTextBox.Text);
            _editor.SelectedText = replacement;
            return true;
        }

        _editor.SelectedText = ReplaceTextBox.Text;
        return true;
    }

    private static RegexOptions BuildRegexReplaceOptions(bool matchCase)
    {
        var options = RegexOptions.Multiline;
        if (!matchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return options;
    }

    private void RefreshHighlightAll()
    {
        ApplyHighlightAllState();
        BeginMatchCountUpdate();
    }

    private void ApplyHighlightAllState()
    {
        _searchHighlightColorizer.IsEnabled = HighlightAllCheckBox.IsChecked == true && !string.IsNullOrEmpty(FindTextBox.Text);
        _searchHighlightColorizer.Query = FindTextBox.Text;
        _searchHighlightColorizer.UseRegex = UseRegexCheckBox.IsChecked == true;
        _searchHighlightColorizer.MatchCase = MatchCaseCheckBox.IsChecked == true;
        _searchHighlightColorizer.WholeWord = WholeWordCheckBox.IsChecked == true;
        _searchHighlightColorizer.BackgroundBrush = GetResourceBrush("HighlightAllBrush", Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
        _searchHighlightColorizer.ForegroundBrush = GetResourceBrush("EditorForegroundBrush", Colors.Black);
        _editor.Redraw();
    }

    private void RemoveHighlightAdorner()
    {
        _searchHighlightColorizer.IsEnabled = false;
        _editor.Redraw();
    }

    private void UpdateMatchCountLabel()
    {
        BeginMatchCountUpdate();
    }

    private void BeginMatchCountUpdate()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            MatchCountText.Text = string.Empty;
            MatchCountText.Visibility = Visibility.Collapsed;
            return;
        }

        MatchCountText.Text = "Searching…";
        MatchCountText.Visibility = Visibility.Visible;

        _matchCountTokenSource?.Cancel();
        _matchCountTokenSource?.Dispose();
        _matchCountTokenSource = new CancellationTokenSource();
        var token = _matchCountTokenSource.Token;
        var text = _editor.Text;
        var currentSelectionStart = _editor.SelectionStart;
        var useRegex = UseRegexCheckBox.IsChecked == true;
        var matchCase = MatchCaseCheckBox.IsChecked == true;
        var wholeWord = WholeWordCheckBox.IsChecked == true;

        _ = Task.Run(
                () =>
                {
                    token.ThrowIfCancellationRequested();
                    return CountMatches(text, query, currentSelectionStart, useRegex, matchCase, wholeWord, token);
                },
                token
            )
            .ContinueWith(
                task =>
                {
                    if (token.IsCancellationRequested || task.IsCanceled || query != FindTextBox.Text)
                    {
                        return;
                    }

                    if (task.IsFaulted)
                    {
                        MatchCountText.Text = "Search failed";
                        MatchCountText.Visibility = Visibility.Visible;
                        return;
                    }

                    var (count, currentMatch) = task.Result;
                    MatchCountText.Text =
                        count == 0 ? "No results"
                        : currentMatch > 0 ? $"{currentMatch} of {count}"
                        : $"{count} matches";
                    MatchCountText.Visibility = Visibility.Visible;
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext()
            );
    }

    private void OpenFindPanel(bool showReplace)
    {
        FindPanel.Visibility = Visibility.Visible;
        _replaceVisible = showReplace;
        _findOptionsVisible = false;
        UpdateFindPanelControls();
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void FindButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: false);
    }

    private void FindAndReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: true);
    }

    private void FindNextMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: false);
        FindNextInternal();
    }

    private void FindPreviousMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: false);
        FindPreviousInternal();
    }

    private void GoToMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        var documentLength = GetEditorDocumentLength();
        var currentLineNumber = _editor.GetLineByOffset(documentLength == 0 ? 0 : Math.Clamp(_editor.CaretOffset, 0, documentLength))?.LineNumber ?? 1;
        var dialog = new GoToLineDialog(currentLineNumber) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.LineNumber > 0)
        {
            var targetLine = dialog.LineNumber - 1;
            var lineCount = _editor.Document?.LineCount ?? 1;
            targetLine = Math.Clamp(targetLine, 0, lineCount - 1);
            var targetDocumentLine = _editor.GetLineByNumber(targetLine + 1);
            if (targetDocumentLine is null)
            {
                return;
            }

            var charIndex = targetDocumentLine.Value.Offset;
            _editor.CaretOffset = charIndex;
            _editor.ScrollToLine(targetLine + 1);
            _editor.Focus();
        }
    }

    private void FindTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text))
        {
            ResetFindHighlight();
            RemoveHighlightAdorner();
            MatchCountText.Visibility = Visibility.Collapsed;
            FindTextBox.Background = GetResourceBrush("InputBackgroundBrush", Color.FromArgb(0xFF, 0x33, 0x33, 0x33));
            return;
        }

        _findRefreshTimer.Stop();
        _findRefreshTimer.Start();
    }

    private void FindTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPreviousInternal();
            }
            else
            {
                FindNextInternal();
            }

            e.Handled = true;
        }
    }

    private void HighlightAllCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        RefreshHighlightAll();
    }

    private void HighlightAllCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        RemoveHighlightAdorner();
        UpdateMatchCountLabel();
    }

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        if (!TryReplaceCurrentMatch())
        {
            FindNextInternal();
            if (!TryReplaceCurrentMatch())
            {
                FlashNotFound();
                return;
            }
        }

        ResetFindHighlight();
        FindNextInternal();
    }

    private async void ReplaceAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var sourceText = _editor.Text;
        var replacementText = ReplaceTextBox.Text;
        var useRegex = UseRegexCheckBox.IsChecked == true;
        var matchCase = MatchCaseCheckBox.IsChecked == true;
        string newText;
        if (useRegex)
        {
            try
            {
                newText = await Task.Run(() => Regex.Replace(sourceText, query, replacementText, BuildRegexReplaceOptions(matchCase)));
            }
            catch
            {
                MessageBox.Show(this, "Invalid regular expression.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            var options = BuildRegexReplaceOptions(matchCase);
            var pattern = WholeWordCheckBox.IsChecked == true ? $@"\b{Regex.Escape(query)}\b" : Regex.Escape(query);
            newText = await Task.Run(() => Regex.Replace(sourceText, pattern, replacementText, options));
        }

        var caretPos = _editor.CaretOffset;
        _editor.Text = newText;
        _editor.CaretOffset = Math.Min(caretPos, newText.Length);
        ResetFindHighlight();
        RemoveHighlightAdorner();
    }

    private void ApplyFindHighlight()
    {
        RefreshHighlightAll();
    }

    private void ResetFindHighlight()
    {
        RefreshHighlightAll();
    }

    private Brush GetResourceBrush(string resourceKey, Color fallbackColor)
    {
        return (Resources.Contains(resourceKey) ? Resources[resourceKey] as Brush : null) ?? (Application.Current.Resources.Contains(resourceKey) ? Application.Current.Resources[resourceKey] as Brush : null) ?? new SolidColorBrush(fallbackColor);
    }

    private static (int Count, int CurrentMatch) CountMatches(string text, string query, int currentSelectionStart, bool useRegex, bool matchCase, bool wholeWord, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return (0, 0);
        }

        var count = 0;
        var currentMatch = 0;

        if (useRegex)
        {
            var options = RegexOptions.Multiline;
            if (!matchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(query, options);
            foreach (Match match in regex.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!match.Success || match.Length <= 0)
                {
                    continue;
                }

                count++;
                if (match.Index == currentSelectionStart)
                {
                    currentMatch = count;
                }
            }

            return (count, currentMatch);
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var pos = 0;
        while (pos < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idx = text.IndexOf(query, pos, comparison);
            if (idx < 0)
            {
                break;
            }

            var valid = !wholeWord || ((idx == 0 || !char.IsLetterOrDigit(text[idx - 1])) && (idx + query.Length >= text.Length || !char.IsLetterOrDigit(text[idx + query.Length])));
            if (valid)
            {
                count++;
                if (idx == currentSelectionStart)
                {
                    currentMatch = count;
                }
            }

            pos = idx + Math.Max(1, query.Length);
        }

        return (count, currentMatch);
    }
}
