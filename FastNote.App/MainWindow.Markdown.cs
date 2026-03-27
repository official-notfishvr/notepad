using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Markdig;

namespace FastNote.App;

public partial class MainWindow
{
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static DocumentKind DetectDocumentKind(string? path, string? title)
    {
        var candidate = !string.IsNullOrWhiteSpace(path) ? path : title;
        var extension = Path.GetExtension(candidate);
        return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase)
            ? DocumentKind.Markdown
            : DocumentKind.PlainText;
    }

    private static bool SupportsMarkdownPreview(DocumentTab? tab)
    {
        if (tab is null)
        {
            return false;
        }

        return tab.IsMarkdownDocument;
    }

    private bool IsMarkdownPreviewActive(DocumentTab? tab)
    {
        return SupportsMarkdownPreview(tab) && tab?.IsMarkdownPreviewEnabled == true;
    }

    private void UpdateMarkdownUi(DocumentTab? tab)
    {
        var supportsMarkdown = SupportsMarkdownPreview(tab);
        var isPreviewActive = IsMarkdownPreviewActive(tab);

        MarkdownPreviewMenuItem.Visibility = supportsMarkdown ? Visibility.Visible : Visibility.Collapsed;
        MarkdownPreviewGlyph.Opacity = isPreviewActive ? 0.85 : 0;
        MarkdownToolbar.Visibility = tab?.CanShowFormattingToolbar == true ? Visibility.Visible : Visibility.Collapsed;
        MarkdownPreviewToggleText.Text = isPreviewActive ? "Edit" : "Preview";
        GoToMenuItem.IsEnabled = tab?.CanUseGoTo != false;
        GoToShortcutText.Opacity = GoToMenuItem.IsEnabled ? 0.5 : 0.25;
        FontMenuItem.Visibility = Visibility.Visible;
        ClearFormattingMenuItem.Visibility = supportsMarkdown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMarkdownPreview(DocumentTab? tab)
    {
        if (!IsMarkdownPreviewActive(tab))
        {
            return;
        }

        var markdown = tab?.EditorDocument?.Text ?? tab?.Text ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(tab?.Path) ? tab?.Title ?? "Markdown preview" : Path.GetFileName(tab.Path);
        var previewCacheKey = $"{_themeMode}\n{title}\n{markdown}";
        if (string.Equals(tab?.MarkdownPreviewCacheKey, previewCacheKey, StringComparison.Ordinal))
        {
            return;
        }

        if (tab is not null)
        {
            tab.MarkdownPreviewCacheKey = previewCacheKey;
        }

        MarkdownPreviewBrowser.NavigateToString(BuildMarkdownPreviewHtml(title, markdown));
    }

    private string BuildMarkdownPreviewHtml(string title, string markdown)
    {
        var isDark = _themeMode == AppThemeMode.Dark;
        var background = ColorToCss(GetThemeColor("EditorBackgroundBrush", isDark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Colors.White));
        var foreground = ColorToCss(GetThemeColor("EditorForegroundBrush", isDark ? Color.FromRgb(0xFC, 0xFC, 0xFC) : Color.FromRgb(0x1A, 0x1A, 0x1A)));
        var muted = ColorToCss(GetThemeColor("EditorMutedBrush", isDark ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0x6C, 0x6C, 0x6C)));
        var border = ColorToCss(GetThemeColor("BorderBrush", isDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xCC, 0xCC, 0xCC)));
        var accent = ColorToCss(GetThemeColor("AccentBrush", isDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0)));
        var surface = ColorToCss(GetThemeColor("SurfaceRaisedBrush", isDark ? Color.FromRgb(0x32, 0x32, 0x32) : Color.FromRgb(0xF3, 0xF3, 0xF3)));
        var htmlBody = Markdown.ToHtml(markdown, _markdownPipeline);

        return $$"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <title>{{WebUtility.HtmlEncode(title)}}</title>
    <style>
        * { box-sizing: border-box; }

        html {
            background: {{background}};
        }

        body {
            margin: 0;
            padding: 28px 32px 40px;
            background: {{background}};
            color: {{foreground}};
            font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
            line-height: 1.65;
        }

        main {
            max-width: 900px;
            margin: 0 auto;
        }

        h1, h2, h3, h4, h5, h6 {
            line-height: 1.2;
            margin: 1.4em 0 0.5em;
        }

        h1, h2 {
            padding-bottom: 0.25em;
            border-bottom: 1px solid {{border}};
        }

        p, ul, ol, pre, table, blockquote {
            margin: 0 0 1em;
        }

        a {
            color: {{accent}};
            text-decoration: none;
        }

        a:hover {
            text-decoration: underline;
        }

        code, pre {
            font-family: Consolas, "Cascadia Code", monospace;
        }

        code {
            background: {{surface}};
            padding: 0.14em 0.34em;
            border-radius: 4px;
        }

        pre {
            padding: 14px 16px;
            overflow: auto;
            border: 1px solid {{border}};
            border-radius: 10px;
            background: {{surface}};
        }

        pre code {
            background: transparent;
            padding: 0;
        }

        blockquote {
            margin-left: 0;
            padding: 0.2em 1em;
            border-left: 4px solid {{accent}};
            color: {{muted}};
            background: {{surface}};
            border-radius: 0 8px 8px 0;
        }

        table {
            border-collapse: collapse;
            width: 100%;
        }

        th, td {
            border: 1px solid {{border}};
            padding: 8px 10px;
            text-align: left;
        }

        th {
            background: {{surface}};
        }

        img {
            max-width: 100%;
            border-radius: 8px;
        }

        hr {
            border: 0;
            border-top: 1px solid {{border}};
            margin: 1.5em 0;
        }
    </style>
</head>
<body>
    <main>{{htmlBody}}</main>
</body>
</html>
""";
    }

    private Color GetThemeColor(string resourceKey, Color fallback)
    {
        if (Resources.Contains(resourceKey) && Resources[resourceKey] is SolidColorBrush localBrush)
        {
            return localBrush.Color;
        }

        if (Application.Current.Resources.Contains(resourceKey) && Application.Current.Resources[resourceKey] is SolidColorBrush appBrush)
        {
            return appBrush.Color;
        }

        return fallback;
    }

    private static string ColorToCss(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void WrapSelectionWithMarkdown(string prefix, string suffix, string placeholder)
    {
        var selectedText = EditorTextBox.SelectedText;
        var content = string.IsNullOrEmpty(selectedText) ? placeholder : selectedText;
        var replacement = $"{prefix}{content}{suffix}";
        var start = EditorTextBox.SelectionStart;
        EditorTextBox.Document.Replace(start, EditorTextBox.SelectionLength, replacement);
        EditorTextBox.Select(start + prefix.Length, content.Length);
        EditorTextBox.Focus();
    }

    private void PrefixSelectionWithMarkdown(string prefix, string placeholder)
    {
        var selectedText = EditorTextBox.SelectedText;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            InsertTextAtCaret(prefix + placeholder);
            return;
        }

        var lines = selectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var prefixed = string.Join(Environment.NewLine, lines.Select(line => prefix + line));
        var start = EditorTextBox.SelectionStart;
        EditorTextBox.Document.Replace(start, EditorTextBox.SelectionLength, prefixed);
        EditorTextBox.Select(start, prefixed.Length);
        EditorTextBox.Focus();
    }

    private void ClearMarkdownFormatting()
    {
        var selectedText = EditorTextBox.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            return;
        }

        var cleaned = selectedText
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal);

        var start = EditorTextBox.SelectionStart;
        EditorTextBox.Document.Replace(start, EditorTextBox.SelectionLength, cleaned);
        EditorTextBox.Select(start, cleaned.Length);
    }

    private void ApplyHeading(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            var selectedText = EditorTextBox.SelectedText;
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return;
            }

            var lines = selectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var cleaned = string.Join(Environment.NewLine, lines.Select(line => line.TrimStart('#', ' ')));
            var start = EditorTextBox.SelectionStart;
            EditorTextBox.Document.Replace(start, EditorTextBox.SelectionLength, cleaned);
            EditorTextBox.Select(start, cleaned.Length);
            return;
        }

        PrefixSelectionWithMarkdown(prefix, "Heading");
    }

    private void ApplyListFormatting(string mode)
    {
        var selectedText = EditorTextBox.SelectedText;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            selectedText = "List item";
        }

        var lines = selectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            switch (mode)
            {
                case "bullet":
                    lines[index] = $"- {lines[index].TrimStart()}";
                    break;
                case "number":
                    lines[index] = $"{index + 1}. {lines[index].TrimStart()}";
                    break;
                case "indent-more":
                    lines[index] = $"    {lines[index]}";
                    break;
                case "indent-less":
                    lines[index] = lines[index].StartsWith("    ", StringComparison.Ordinal) ? lines[index][4..] : lines[index].TrimStart();
                    break;
            }
        }

        var replacement = string.Join(Environment.NewLine, lines);
        var start = EditorTextBox.SelectionStart;
        EditorTextBox.Document.Replace(start, EditorTextBox.SelectionLength, replacement);
        EditorTextBox.Select(start, replacement.Length);
        EditorTextBox.Focus();
    }

    private void HeadingButton_OnClick(object sender, RoutedEventArgs e)
    {
        ListMenuPopup.IsOpen = false;
        HeadingMenuPopup.IsOpen = !HeadingMenuPopup.IsOpen;
    }

    private void BoldButton_OnClick(object sender, RoutedEventArgs e) => WrapSelectionWithMarkdown("**", "**", "bold text");

    private void ItalicButton_OnClick(object sender, RoutedEventArgs e) => WrapSelectionWithMarkdown("*", "*", "italic text");

    private void StrikeButton_OnClick(object sender, RoutedEventArgs e) => WrapSelectionWithMarkdown("~~", "~~", "strikethrough");

    private void LinkButton_OnClick(object sender, RoutedEventArgs e) => WrapSelectionWithMarkdown("[", "](https://)", "link text");

    private void ListButton_OnClick(object sender, RoutedEventArgs e)
    {
        HeadingMenuPopup.IsOpen = false;
        ListMenuPopup.IsOpen = !ListMenuPopup.IsOpen;
    }

    private void TableButton_OnClick(object sender, RoutedEventArgs e) => InsertTextAtCaret($"| Column 1 | Column 2 |{Environment.NewLine}| --- | --- |{Environment.NewLine}| Value | Value |");

    private void HeadingLevelMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        HeadingMenuPopup.IsOpen = false;
        if (sender is Button { Tag: string prefix })
        {
            ApplyHeading(prefix);
        }
    }

    private void ListMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ListMenuPopup.IsOpen = false;
        if (sender is Button { Tag: string mode })
        {
            ApplyListFormatting(mode);
        }
    }
}
