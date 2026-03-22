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
        return EditorTextBox.Document?.TextLength ?? 0;
    }

    private bool IsDocumentOverLimit(int limit)
    {
        return GetEditorDocumentLength() > limit;
    }

    private void FindNextInternal()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var text = EditorTextBox.Text;
        var start = EditorTextBox.SelectionStart + EditorTextBox.SelectionLength;

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            index = FindWithRegex(text, query, start, forward: true);
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: true, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            ScrollToMatchAndSelect(index, query.Length);
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

        var text = EditorTextBox.Text;
        var start = Math.Max(0, EditorTextBox.SelectionStart - 1);

        int index;
        if (UseRegexCheckBox.IsChecked == true)
        {
            index = FindWithRegex(text, query, start, forward: false);
        }
        else
        {
            var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            index = FindWithOptions(text, query, start, forward: false, comparison, WholeWordCheckBox.IsChecked == true);
        }

        if (index >= 0)
        {
            ScrollToMatchAndSelect(index, query.Length);
        }
        else
        {
            FlashNotFound();
        }

        RefreshHighlightAll();
    }

    private void ScrollToMatchAndSelect(int index, int length)
    {
        EditorTextBox.Select(index, length);
        var documentLength = GetEditorDocumentLength();
        var line = EditorTextBox.Document?.GetLineByOffset(documentLength == 0 ? 0 : Math.Clamp(index, 0, documentLength));
        if (line is not null)
        {
            EditorTextBox.ScrollToLine(line.LineNumber);
        }

        ApplyFindHighlight();
        UpdateMatchCountLabel();
    }

    private void FlashNotFound()
    {
        FindTextBox.Background = GetResourceBrush("FindNotFoundBrush", Color.FromArgb(0x55, 0xC4, 0x2B, 0x1C));
        FindTextBox.Dispatcher.BeginInvoke(() =>
        {
            FindTextBox.Background = GetResourceBrush("InputBackgroundBrush", Color.FromArgb(0xFF, 0x33, 0x33, 0x33));
        }, System.Windows.Threading.DispatcherPriority.Background);
        ResetFindHighlight();
        UpdateMatchCountLabel();
    }

    private static int FindWithOptions(string text, string query, int start, bool forward, StringComparison comparison, bool wholeWord)
    {
        int index;
        if (forward)
        {
            index = text.IndexOf(query, start, comparison);
            if (index < 0)
            {
                index = text.IndexOf(query, comparison);
            }
        }
        else
        {
            index = start > 0 ? text.LastIndexOf(query, start, comparison) : -1;
            if (index < 0)
            {
                index = text.LastIndexOf(query, comparison);
            }
        }

        if (index >= 0 && wholeWord)
        {
            if ((index > 0 && char.IsLetterOrDigit(text[index - 1])) ||
                (index + query.Length < text.Length && char.IsLetterOrDigit(text[index + query.Length])))
            {
                return -1;
            }
        }

        return index;
    }

    private static int FindWithRegex(string text, string pattern, int start, bool forward)
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

                return match.Success ? match.Index : -1;
            }
            else
            {
                var matches = regex.Matches(text[..start]);
                return matches.Count > 0 ? matches[^1].Index : -1;
            }
        }
        catch
        {
            return -1;
        }
    }

    private List<(int Start, int Length)> FindAllMatches(string text, string query)
    {
        var results = new List<(int, int)>();
        if (string.IsNullOrEmpty(query))
        {
            return results;
        }

        try
        {
            if (UseRegexCheckBox.IsChecked == true)
            {
                var regex = new Regex(query, RegexOptions.Multiline);
                foreach (Match m in regex.Matches(text))
                {
                    results.Add((m.Index, m.Length));
                }
            }
            else
            {
                var comparison = MatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var wholeWord = WholeWordCheckBox.IsChecked == true;
                var pos = 0;
                while (pos < text.Length)
                {
                    var idx = text.IndexOf(query, pos, comparison);
                    if (idx < 0)
                    {
                        break;
                    }

                    var valid = !wholeWord ||
                        ((idx == 0 || !char.IsLetterOrDigit(text[idx - 1])) &&
                         (idx + query.Length >= text.Length || !char.IsLetterOrDigit(text[idx + query.Length])));

                    if (valid)
                    {
                        results.Add((idx, query.Length));
                    }

                    pos = idx + Math.Max(1, query.Length);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private void RefreshHighlightAll()
    {
        _searchHighlightColorizer.IsEnabled = HighlightAllCheckBox.IsChecked == true && !string.IsNullOrEmpty(FindTextBox.Text);
        _searchHighlightColorizer.Query = FindTextBox.Text;
        _searchHighlightColorizer.UseRegex = UseRegexCheckBox.IsChecked == true;
        _searchHighlightColorizer.MatchCase = MatchCaseCheckBox.IsChecked == true;
        _searchHighlightColorizer.WholeWord = WholeWordCheckBox.IsChecked == true;
        _searchHighlightColorizer.BackgroundBrush = GetResourceBrush("HighlightAllBrush", Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
        _searchHighlightColorizer.ForegroundBrush = GetResourceBrush("EditorForegroundBrush", Colors.Black);
        EditorTextBox.TextArea.TextView.Redraw();
        UpdateMatchCountLabel();
    }

    private void RemoveHighlightAdorner()
    {
        _searchHighlightColorizer.IsEnabled = false;
        EditorTextBox.TextArea.TextView.Redraw();
    }

    private void UpdateMatchCountLabel()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            MatchCountText.Text = string.Empty;
            MatchCountText.Visibility = Visibility.Collapsed;
            return;
        }

        if (IsDocumentOverLimit(MatchCountSoftLimitCharacters))
        {
            MatchCountText.Text = "Match count disabled for large files";
            MatchCountText.Visibility = Visibility.Visible;
            return;
        }

        var matches = FindAllMatches(EditorTextBox.Text, query);
        if (matches.Count == 0)
        {
            MatchCountText.Text = "No results";
        }
        else
        {
            var currentSel = EditorTextBox.SelectionStart;
            var currentMatch = matches.FindIndex(m => m.Start == currentSel) + 1;
            MatchCountText.Text = currentMatch > 0
                ? $"{currentMatch} of {matches.Count}"
                : $"{matches.Count} matches";
        }

        MatchCountText.Visibility = Visibility.Visible;
    }

    private void OpenFindPanel(bool showReplace)
    {
        FindPanel.Visibility = Visibility.Visible;
        _replaceVisible = showReplace;
        ReplaceRowPanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
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

    private void GoToMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        var documentLength = GetEditorDocumentLength();
        var currentLineNumber = EditorTextBox.Document?.GetLineByOffset(documentLength == 0 ? 0 : Math.Clamp(EditorTextBox.CaretOffset, 0, documentLength)).LineNumber ?? 1;
        var dialog = new GoToLineDialog(currentLineNumber)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.LineNumber > 0)
        {
            var targetLine = dialog.LineNumber - 1;
            var lineCount = EditorTextBox.Document?.LineCount ?? 1;
            targetLine = Math.Clamp(targetLine, 0, lineCount - 1);
            var targetDocumentLine = EditorTextBox.Document?.GetLineByNumber(targetLine + 1);
            if (targetDocumentLine is null)
            {
                return;
            }

            var charIndex = targetDocumentLine.Offset;
            EditorTextBox.CaretOffset = charIndex;
            EditorTextBox.ScrollToLine(targetLine + 1);
            EditorTextBox.Focus();
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

        if (IsDocumentOverLimit(LiveSearchSoftLimitCharacters))
        {
            MatchCountText.Text = "Press Enter to search in large files";
            MatchCountText.Visibility = Visibility.Visible;
            RefreshHighlightAll();
            return;
        }

        RefreshHighlightAll();
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

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e) => FindNextInternal();
    private void FindPreviousButton_OnClick(object sender, RoutedEventArgs e) => FindPreviousInternal();

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        if (EditorTextBox.SelectionLength > 0 && string.Equals(EditorTextBox.SelectedText, query, StringComparison.OrdinalIgnoreCase))
        {
            EditorTextBox.SelectedText = ReplaceTextBox.Text;
        }

        ResetFindHighlight();
        FindNextInternal();
    }

    private void ReplaceAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        if (IsDocumentOverLimit(ReplaceAllSoftLimitCharacters))
        {
            MessageBox.Show(
                this,
                "Replace All is disabled for very large files because it can allocate another full copy of the document and crash the app.",
                "Notepad",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string newText;
        if (UseRegexCheckBox.IsChecked == true)
        {
            try
            {
                newText = Regex.Replace(EditorTextBox.Text, query, ReplaceTextBox.Text, RegexOptions.Multiline);
            }
            catch
            {
                MessageBox.Show(this, "Invalid regular expression.", "Notepad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            var options = MatchCaseCheckBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            newText = Regex.Replace(EditorTextBox.Text, Regex.Escape(query), ReplaceTextBox.Text, options);
        }

        var caretPos = EditorTextBox.CaretOffset;
        EditorTextBox.Text = newText;
        EditorTextBox.CaretOffset = Math.Min(caretPos, newText.Length);
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
        return Application.Current.Resources[resourceKey] as Brush ?? new SolidColorBrush(fallbackColor);
    }
}
