using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FastNote.App;

public partial class MainWindow
{
    private void FindNextInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

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
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
            EditorTextBox.ScrollToLine(EditorTextBox.GetLineIndexFromCharacterIndex(index));
            ApplyFindHighlight();
        }
        else
        {
            ResetFindHighlight();
        }
    }

    private void FindPreviousInternal()
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            return;
        }

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
            EditorTextBox.Focus();
            EditorTextBox.Select(index, query.Length);
            EditorTextBox.ScrollToLine(EditorTextBox.GetLineIndexFromCharacterIndex(index));
            ApplyFindHighlight();
        }
        else
        {
            ResetFindHighlight();
        }
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

    private void OpenFindPanel(bool showReplace)
    {
        FindPanel.Visibility = Visibility.Visible;
        _replaceVisible = showReplace;
        ReplaceRowPanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private async void FindButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: false);
    }

    private async void FindAndReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

        EditMenuPopup.IsOpen = false;
        OpenFindPanel(showReplace: true);
    }

    private async void GoToMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EditMenuPopup.IsOpen = false;

        if (!await EnsureEditableTabAsync(GetActiveTab()))
        {
            return;
        }

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

    private void FindTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(FindTextBox.Text))
        {
            FindNextInternal();
        }
        else
        {
            ResetFindHighlight();
        }
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

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e) => FindNextInternal();
    private void FindPreviousButton_OnClick(object sender, RoutedEventArgs e) => FindPreviousInternal();

    private void ReplaceOneButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

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
        if (GetActiveTab()?.Mode == DocumentMode.LargePreview)
        {
            ShowLargeFileEditingMessage();
            return;
        }

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
        EditorTextBox.SelectionBrush = GetResourceBrush("AccentSoftBrush", Color.FromArgb(0x22, 0x00, 0x67, 0xC0));
        EditorTextBox.SelectionTextBrush = GetResourceBrush("EditorForegroundBrush", Colors.White);
    }

    private Brush GetResourceBrush(string resourceKey, Color fallbackColor)
    {
        return Application.Current.Resources[resourceKey] as Brush ?? new SolidColorBrush(fallbackColor);
    }
}
