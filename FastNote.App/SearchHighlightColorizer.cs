using System.Text.RegularExpressions;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App;

internal sealed class SearchHighlightColorizer : DocumentColorizingTransformer
{
    public bool IsEnabled { get; set; }
    public string Query { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public Brush? BackgroundBrush { get; set; }
    public Brush? ForegroundBrush { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!IsEnabled || string.IsNullOrEmpty(Query))
        {
            return;
        }

        var lineText = CurrentContext.Document.GetText(line);
        foreach (var match in GetMatches(lineText))
        {
            var startOffset = line.Offset + match.Start;
            var endOffset = startOffset + match.Length;
            ChangeLinePart(
                startOffset,
                endOffset,
                element =>
                {
                    if (BackgroundBrush is not null)
                    {
                        element.TextRunProperties.SetBackgroundBrush(BackgroundBrush);
                    }

                    if (ForegroundBrush is not null)
                    {
                        element.TextRunProperties.SetForegroundBrush(ForegroundBrush);
                    }
                }
            );
        }
    }

    private IEnumerable<(int Start, int Length)> GetMatches(string lineText)
    {
        if (string.IsNullOrEmpty(lineText))
        {
            yield break;
        }

        if (UseRegex)
        {
            Regex regex;
            try
            {
                var options = RegexOptions.Multiline;
                if (!MatchCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                regex = new Regex(Query, options);
            }
            catch
            {
                yield break;
            }

            foreach (Match match in regex.Matches(lineText))
            {
                if (match.Success && match.Length > 0)
                {
                    yield return (match.Index, match.Length);
                }
            }

            yield break;
        }

        var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var searchStart = 0;
        while (searchStart < lineText.Length)
        {
            var index = lineText.IndexOf(Query, searchStart, comparison);
            if (index < 0)
            {
                yield break;
            }

            if (!WholeWord || IsWholeWordMatch(lineText, index, Query.Length))
            {
                yield return (index, Query.Length);
            }

            searchStart = index + Math.Max(1, Query.Length);
        }
    }

    private static bool IsWholeWordMatch(string text, int start, int length)
    {
        var beforeValid = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        var afterIndex = start + length;
        var afterValid = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
        return beforeValid && afterValid;
    }
}
