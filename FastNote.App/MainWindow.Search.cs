using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow
{
    private HighlightAllAdorner? _highlightAdorner;

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
        EditorTextBox.ScrollToLine(EditorTextBox.GetLineIndexFromCharacterIndex(index));
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
        if (HighlightAllCheckBox.IsChecked != true)
        {
            RemoveHighlightAdorner();
            return;
        }

        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            RemoveHighlightAdorner();
            return;
        }

        var matches = FindAllMatches(EditorTextBox.Text, query);
        var layer = AdornerLayer.GetAdornerLayer(EditorTextBox);
        if (layer is null)
        {
            return;
        }

        RemoveHighlightAdorner();

        if (matches.Count == 0)
        {
            return;
        }

        var highlightBrush = GetResourceBrush("HighlightAllBrush", Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
        _highlightAdorner = new HighlightAllAdorner(EditorTextBox, matches, highlightBrush);
        layer.Add(_highlightAdorner);
    }

    private void RemoveHighlightAdorner()
    {
        if (_highlightAdorner is null)
        {
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(EditorTextBox);
        layer?.Remove(_highlightAdorner);
        _highlightAdorner = null;
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

        var dialog = new GoToLineDialog(EditorTextBox.GetLineIndexFromCharacterIndex(EditorTextBox.CaretIndex) + 1)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.LineNumber > 0)
        {
            var targetLine = dialog.LineNumber - 1;
            var lineCount = EditorTextBox.LineCount;
            targetLine = Math.Clamp(targetLine, 0, lineCount - 1);
            var charIndex = EditorTextBox.GetCharacterIndexFromLineIndex(targetLine);
            EditorTextBox.CaretIndex = charIndex;
            EditorTextBox.ScrollToLine(targetLine);
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

        FindNextInternal();
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

        var caretPos = EditorTextBox.CaretIndex;
        EditorTextBox.Text = newText;
        EditorTextBox.CaretIndex = Math.Min(caretPos, newText.Length);
        ResetFindHighlight();
        RemoveHighlightAdorner();
    }

    private void ApplyFindHighlight()
    {
        if (FindPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        EditorTextBox.SelectionBrush = GetResourceBrush("FindHighlightBrush", Colors.DodgerBlue);
        EditorTextBox.SelectionTextBrush = GetResourceBrush("FindHighlightForegroundBrush", Colors.White);
    }

    private void ResetFindHighlight()
    {
        EditorTextBox.SelectionBrush = GetResourceBrush("AccentSoftBrush", Color.FromArgb(0x1A, 0x00, 0x67, 0xC0));
        EditorTextBox.SelectionTextBrush = GetResourceBrush("EditorForegroundBrush", Colors.White);
    }

    private Brush GetResourceBrush(string resourceKey, Color fallbackColor)
    {
        return Application.Current.Resources[resourceKey] as Brush ?? new SolidColorBrush(fallbackColor);
    }
}

internal sealed class HighlightAllAdorner : Adorner
{
    private readonly List<(int Start, int Length)> _matches;
    private readonly Brush _brush;
    private readonly TextBox _textBox;

    public HighlightAllAdorner(TextBox textBox, List<(int Start, int Length)> matches, Brush brush)
        : base(textBox)
    {
        _textBox = textBox;
        _matches = matches;
        _brush = brush;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (start, length) in _matches)
        {
            if (start < 0 || start + length > _textBox.Text.Length)
            {
                continue;
            }

            try
            {
                var startRect = _textBox.GetRectFromCharacterIndex(start);
                var endRect = _textBox.GetRectFromCharacterIndex(start + length);

                if (startRect.IsEmpty || endRect.IsEmpty)
                {
                    continue;
                }

                if (Math.Abs(startRect.Top - endRect.Top) < 2)
                {
                    dc.DrawRectangle(_brush, null, new Rect(startRect.Left, startRect.Top, Math.Max(2, endRect.Left - startRect.Left), startRect.Height));
                }
                else
                {
                    dc.DrawRectangle(_brush, null, new Rect(startRect.Left, startRect.Top, Math.Max(2, _textBox.ActualWidth - startRect.Left - 4), startRect.Height));
                }
            }
            catch
            {
            }
        }
    }
}