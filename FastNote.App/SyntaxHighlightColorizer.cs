using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App;

internal enum SyntaxLanguage
{
    None,
    Json,
    CSharp,
    Cpp,
    Python,
    Xml,
    Html,
    Css,
    JavaScript,
}

internal sealed class SyntaxHighlightColorizer : DocumentColorizingTransformer // not using AvalonEdit Highlighting because its bad for dark mode
{
    private static readonly Regex JsonPropertyRegex = new("\"(?:\\\\.|[^\"\\\\])*\"(?=\\s*:)", RegexOptions.Compiled);
    private static readonly Regex JsonStringRegex = new("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);
    private static readonly Regex JsonNumberRegex = new("-?(?:0|[1-9]\\d*)(?:\\.\\d+)?(?:[eE][+-]?\\d+)?", RegexOptions.Compiled);
    private static readonly Regex JsonLiteralRegex = new("\\b(?:true|false|null)\\b", RegexOptions.Compiled);
    private static readonly Regex JsonPunctuationRegex = new("[{}\\[\\]:,]", RegexOptions.Compiled);
    private static readonly Regex XmlCommentRegex = new("<!--.*?-->", RegexOptions.Compiled);
    private static readonly Regex XmlTagRegex = new("</?\\s*([A-Za-z_:][\\w:.-]*)", RegexOptions.Compiled);
    private static readonly Regex XmlAttributeRegex = new("\\b([A-Za-z_:][\\w:.-]*)(?=\\s*=)", RegexOptions.Compiled);
    private static readonly Regex XmlAttributeValueRegex = new("\"(?:\\\\.|[^\"])*\"|'(?:\\\\.|[^'])*'", RegexOptions.Compiled);
    private static readonly Regex XmlEntityRegex = new("&[A-Za-z0-9#]+;", RegexOptions.Compiled);
    private static readonly Regex CssPropertyRegex = new("(?<![.#@])\\b([A-Za-z-]+)(?=\\s*:)", RegexOptions.Compiled);
    private static readonly Regex CssSelectorRegex = new("([.#]?[A-Za-z_][\\w\\-:#.\\[\\]=\"']*)(?=\\s*\\{)", RegexOptions.Compiled);
    private static readonly Regex CssAtRuleRegex = new("@[A-Za-z-]+", RegexOptions.Compiled);
    private static readonly Regex CssHexColorRegex = new("#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\\b", RegexOptions.Compiled);

    private static readonly HashSet<string> CSharpKeywords = CreateWordSet(
        "abstract",
        "as",
        "async",
        "await",
        "base",
        "break",
        "case",
        "catch",
        "checked",
        "class",
        "const",
        "continue",
        "default",
        "delegate",
        "do",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "finally",
        "fixed",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "interface",
        "internal",
        "is",
        "lock",
        "namespace",
        "new",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "record",
        "ref",
        "return",
        "sealed",
        "sizeof",
        "stackalloc",
        "static",
        "struct",
        "switch",
        "throw",
        "try",
        "typeof",
        "unchecked",
        "unsafe",
        "using",
        "virtual",
        "volatile",
        "while"
    );
    private static readonly HashSet<string> CSharpTypes = CreateWordSet("bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "void");
    private static readonly HashSet<string> CSharpLiterals = CreateWordSet("true", "false", "null", "this", "base");
    private static readonly HashSet<string> CppKeywords = CreateWordSet(
        "alignas",
        "alignof",
        "asm",
        "auto",
        "break",
        "case",
        "catch",
        "class",
        "const",
        "constexpr",
        "continue",
        "default",
        "delete",
        "do",
        "else",
        "enum",
        "explicit",
        "export",
        "extern",
        "final",
        "for",
        "friend",
        "goto",
        "if",
        "inline",
        "mutable",
        "namespace",
        "new",
        "noexcept",
        "operator",
        "override",
        "private",
        "protected",
        "public",
        "return",
        "sizeof",
        "static",
        "struct",
        "switch",
        "template",
        "throw",
        "try",
        "typedef",
        "typename",
        "union",
        "using",
        "virtual",
        "volatile",
        "while"
    );
    private static readonly HashSet<string> CppTypes = CreateWordSet("bool", "char", "char8_t", "char16_t", "char32_t", "double", "float", "int", "long", "short", "signed", "size_t", "std", "string", "unsigned", "void", "wchar_t", "reinterpret_cast", "static_cast", "const_cast", "dynamic_cast");
    private static readonly HashSet<string> CppLiterals = CreateWordSet("true", "false", "nullptr", "this");
    private static readonly HashSet<string> PythonKeywords = CreateWordSet(
        "and",
        "as",
        "assert",
        "async",
        "await",
        "break",
        "case",
        "class",
        "continue",
        "def",
        "del",
        "elif",
        "else",
        "except",
        "finally",
        "for",
        "from",
        "global",
        "if",
        "import",
        "in",
        "is",
        "lambda",
        "match",
        "nonlocal",
        "not",
        "or",
        "pass",
        "raise",
        "return",
        "try",
        "while",
        "with",
        "yield"
    );
    private static readonly HashSet<string> PythonTypes = CreateWordSet("bool", "bytes", "dict", "float", "int", "list", "object", "set", "str", "tuple");
    private static readonly HashSet<string> PythonLiterals = CreateWordSet("True", "False", "None", "self", "cls");
    private static readonly HashSet<string> JavaScriptKeywords = CreateWordSet(
        "async",
        "await",
        "break",
        "case",
        "catch",
        "class",
        "const",
        "continue",
        "debugger",
        "default",
        "delete",
        "do",
        "else",
        "export",
        "extends",
        "finally",
        "for",
        "function",
        "if",
        "import",
        "in",
        "instanceof",
        "let",
        "new",
        "of",
        "return",
        "static",
        "super",
        "switch",
        "throw",
        "try",
        "typeof",
        "var",
        "while",
        "yield"
    );
    private static readonly HashSet<string> JavaScriptTypes = CreateWordSet("Array", "Boolean", "Date", "Map", "Number", "Object", "Promise", "RegExp", "Set", "String");
    private static readonly HashSet<string> JavaScriptLiterals = CreateWordSet("true", "false", "null", "undefined", "this");

    public SyntaxLanguage Language { get; set; }
    public bool IsDarkTheme { get; set; } = true;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Language == SyntaxLanguage.None)
        {
            return;
        }

        var lineText = CurrentContext.Document.GetText(line);
        if (string.IsNullOrEmpty(lineText))
        {
            return;
        }

        foreach (var token in Tokenize(lineText))
        {
            ChangeLinePart(
                line.Offset + token.Start,
                line.Offset + token.Start + token.Length,
                element =>
                {
                    element.TextRunProperties.SetForegroundBrush(GetBrush(token.Kind));
                }
            );
        }
    }

    private IEnumerable<SyntaxToken> Tokenize(string lineText)
    {
        var tokens = Language switch
        {
            SyntaxLanguage.Json => TokenizeJson(lineText),
            SyntaxLanguage.CSharp => TokenizeCLike(lineText, CSharpKeywords, CSharpTypes, CSharpLiterals, true, true),
            SyntaxLanguage.Cpp => TokenizeCLike(lineText, CppKeywords, CppTypes, CppLiterals, false, true),
            SyntaxLanguage.Python => TokenizePython(lineText),
            SyntaxLanguage.JavaScript => TokenizeCLike(lineText, JavaScriptKeywords, JavaScriptTypes, JavaScriptLiterals, false, false, true),
            SyntaxLanguage.Xml or SyntaxLanguage.Html => TokenizeXml(lineText),
            SyntaxLanguage.Css => TokenizeCss(lineText),
            _ => [],
        };

        return tokens.OrderBy(token => token.Start).ThenByDescending(token => token.Length);
    }

    private static List<SyntaxToken> TokenizeJson(string lineText)
    {
        var tokens = new List<SyntaxToken>();
        AddRegexTokens(tokens, JsonPropertyRegex, lineText, SyntaxTokenKind.Property);
        AddRegexTokens(tokens, JsonStringRegex, lineText, SyntaxTokenKind.String);
        AddRegexTokens(tokens, JsonNumberRegex, lineText, SyntaxTokenKind.Number);
        AddRegexTokens(tokens, JsonLiteralRegex, lineText, SyntaxTokenKind.Keyword);
        AddRegexTokens(tokens, JsonPunctuationRegex, lineText, SyntaxTokenKind.Punctuation);
        return tokens;
    }

    private static List<SyntaxToken> TokenizeXml(string lineText)
    {
        var tokens = new List<SyntaxToken>();
        AddRegexTokens(tokens, XmlCommentRegex, lineText, SyntaxTokenKind.Comment);
        AddRegexTokens(tokens, XmlTagRegex, lineText, SyntaxTokenKind.Tag, 1, tokens);
        AddRegexTokens(tokens, XmlAttributeRegex, lineText, SyntaxTokenKind.Attribute, 1, tokens);
        AddRegexTokens(tokens, XmlAttributeValueRegex, lineText, SyntaxTokenKind.String, skipInside: tokens);
        AddRegexTokens(tokens, XmlEntityRegex, lineText, SyntaxTokenKind.Keyword, skipInside: tokens);
        return tokens;
    }

    private static List<SyntaxToken> TokenizeCss(string lineText)
    {
        var tokens = new List<SyntaxToken>();
        AddInlineCommentsAndStrings(tokens, lineText, false, false, false);
        AddRegexTokens(tokens, CssAtRuleRegex, lineText, SyntaxTokenKind.Keyword, skipInside: tokens);
        AddRegexTokens(tokens, CssSelectorRegex, lineText, SyntaxTokenKind.Tag, 1, tokens);
        AddRegexTokens(tokens, CssPropertyRegex, lineText, SyntaxTokenKind.Property, 1, tokens);
        AddRegexTokens(tokens, CssHexColorRegex, lineText, SyntaxTokenKind.Number, skipInside: tokens);
        return tokens;
    }

    private static List<SyntaxToken> TokenizePython(string lineText)
    {
        var tokens = new List<SyntaxToken>();
        AddHashComment(tokens, lineText);
        AddPythonStrings(tokens, lineText);
        AddWordTokens(tokens, lineText, PythonKeywords, SyntaxTokenKind.Keyword);
        AddWordTokens(tokens, lineText, PythonTypes, SyntaxTokenKind.Type);
        AddWordTokens(tokens, lineText, PythonLiterals, SyntaxTokenKind.Keyword);
        AddFunctionTokens(tokens, lineText);
        AddDecoratorTokens(tokens, lineText);
        AddNumberTokens(tokens, lineText);
        return tokens;
    }

    private static List<SyntaxToken> TokenizeCLike(string lineText, HashSet<string> keywords, HashSet<string> types, HashSet<string> literals, bool supportsVerbatimStrings, bool supportsPreprocessor, bool supportsBacktickStrings = false)
    {
        var tokens = new List<SyntaxToken>();
        var firstNonWhitespace = lineText.TakeWhile(char.IsWhiteSpace).Count();
        if (supportsPreprocessor && firstNonWhitespace < lineText.Length && lineText[firstNonWhitespace] == '#')
        {
            tokens.Add(new SyntaxToken(firstNonWhitespace, lineText.Length - firstNonWhitespace, SyntaxTokenKind.Preprocessor));
            return tokens;
        }

        AddInlineCommentsAndStrings(tokens, lineText, supportsVerbatimStrings, supportsBacktickStrings, true);
        AddWordTokens(tokens, lineText, keywords, SyntaxTokenKind.Keyword);
        AddWordTokens(tokens, lineText, types, SyntaxTokenKind.Type);
        AddWordTokens(tokens, lineText, literals, SyntaxTokenKind.Keyword);
        AddFunctionTokens(tokens, lineText);
        AddTypeLikeTokens(tokens, lineText);
        AddNumberTokens(tokens, lineText);
        return tokens;
    }

    private static void AddInlineCommentsAndStrings(List<SyntaxToken> tokens, string lineText, bool supportsVerbatimStrings, bool supportsBacktickStrings, bool supportsSlashComments)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index))
            {
                index++;
                continue;
            }

            if (supportsSlashComments && index + 1 < lineText.Length && lineText[index] == '/' && lineText[index + 1] == '/')
            {
                tokens.Add(new SyntaxToken(index, lineText.Length - index, SyntaxTokenKind.Comment));
                break;
            }

            if (index + 1 < lineText.Length && lineText[index] == '/' && lineText[index + 1] == '*')
            {
                var end = lineText.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? lineText.Length : end + 2;
                tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.Comment));
                index = end;
                continue;
            }

            if (supportsVerbatimStrings && index + 1 < lineText.Length && lineText[index] == '@' && lineText[index + 1] == '"')
            {
                var end = lineText.IndexOf('"', index + 2);
                end = end < 0 ? lineText.Length : end + 1;
                tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.String));
                index = end;
                continue;
            }

            if (lineText[index] == '"' || lineText[index] == '\'' || (supportsBacktickStrings && lineText[index] == '`'))
            {
                var quote = lineText[index];
                var end = index + 1;
                while (end < lineText.Length)
                {
                    if (lineText[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (lineText[end] == quote)
                    {
                        end++;
                        break;
                    }

                    end++;
                }

                tokens.Add(new SyntaxToken(index, Math.Min(end, lineText.Length) - index, SyntaxTokenKind.String));
                index = Math.Min(end, lineText.Length);
                continue;
            }

            index++;
        }
    }

    private static void AddHashComment(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; index++)
        {
            if (IsOccupied(tokens, index))
            {
                continue;
            }

            if (lineText[index] == '#')
            {
                tokens.Add(new SyntaxToken(index, lineText.Length - index, SyntaxTokenKind.Comment));
                return;
            }
        }
    }

    private static void AddPythonStrings(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index))
            {
                index++;
                continue;
            }

            if (lineText[index] == '\'' || lineText[index] == '"')
            {
                var quote = lineText[index];
                var end = index + 1;
                while (end < lineText.Length)
                {
                    if (lineText[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (lineText[end] == quote)
                    {
                        end++;
                        break;
                    }

                    end++;
                }

                tokens.Add(new SyntaxToken(index, Math.Min(end, lineText.Length) - index, SyntaxTokenKind.String));
                index = Math.Min(end, lineText.Length);
                continue;
            }

            index++;
        }
    }

    private static void AddDecoratorTokens(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; index++)
        {
            if (IsOccupied(tokens, index) || lineText[index] != '@')
            {
                continue;
            }

            var end = index + 1;
            while (end < lineText.Length && (char.IsLetterOrDigit(lineText[end]) || lineText[end] is '_' or '.'))
            {
                end++;
            }

            if (end > index + 1)
            {
                tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.Preprocessor));
            }
        }
    }

    private static void AddWordTokens(List<SyntaxToken> tokens, string lineText, HashSet<string> words, SyntaxTokenKind kind)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index) || !IsIdentifierStart(lineText[index]))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lineText.Length && IsIdentifierPart(lineText[end]))
            {
                end++;
            }

            if (words.Contains(lineText[index..end]))
            {
                tokens.Add(new SyntaxToken(index, end - index, kind));
            }

            index = end;
        }
    }

    private static void AddFunctionTokens(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index) || !IsIdentifierStart(lineText[index]))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lineText.Length && IsIdentifierPart(lineText[end]))
            {
                end++;
            }

            var next = end;
            while (next < lineText.Length && char.IsWhiteSpace(lineText[next]))
            {
                next++;
            }

            if (next < lineText.Length && lineText[next] == '(')
            {
                tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.Function));
            }

            index = end;
        }
    }

    private static void AddNumberTokens(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index) || !char.IsDigit(lineText[index]))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lineText.Length && (char.IsLetterOrDigit(lineText[end]) || lineText[end] is '.' or '_' or 'x' or 'X'))
            {
                end++;
            }

            tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.Number));
            index = end;
        }
    }

    private static void AddTypeLikeTokens(List<SyntaxToken> tokens, string lineText)
    {
        for (var index = 0; index < lineText.Length; )
        {
            if (IsOccupied(tokens, index) || !char.IsUpper(lineText[index]))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lineText.Length && IsIdentifierPart(lineText[end]))
            {
                end++;
            }

            var next = end;
            while (next < lineText.Length && char.IsWhiteSpace(lineText[next]))
            {
                next++;
            }

            if (next < lineText.Length && lineText[next] == '(')
            {
                index = end;
                continue;
            }

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(lineText[previous]))
            {
                previous--;
            }

            var looksQualified = next + 1 < lineText.Length && lineText[next] == ':' && lineText[next + 1] == ':';
            looksQualified |= previous > 0 && lineText[previous] == ':' && lineText[previous - 1] == ':';

            if (looksQualified || index == 0 || previous < 0 || ".<(:,=*".Contains(lineText[previous]))
            {
                tokens.Add(new SyntaxToken(index, end - index, SyntaxTokenKind.Type));
            }

            index = end;
        }
    }

    private static void AddRegexTokens(List<SyntaxToken> tokens, Regex regex, string lineText, SyntaxTokenKind kind, int groupIndex = 0, IEnumerable<SyntaxToken>? skipInside = null)
    {
        foreach (Match match in regex.Matches(lineText))
        {
            var group = match.Groups[groupIndex];
            if (!group.Success || group.Length == 0 || IsOccupied(skipInside ?? tokens, group.Index))
            {
                continue;
            }

            tokens.Add(new SyntaxToken(group.Index, group.Length, kind));
        }
    }

    private SolidColorBrush GetBrush(SyntaxTokenKind kind)
    {
        var color = (Language, IsDarkTheme, kind) switch
        {
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Comment) => Color.FromRgb(0x6E, 0x85, 0x76),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Keyword) => Color.FromRgb(0xC8, 0x8B, 0xFF),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Type) => Color.FromRgb(0x67, 0xBE, 0xFF),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.String) => Color.FromRgb(0xE4, 0xC0, 0x75),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Number) => Color.FromRgb(0xD8, 0xB4, 0xFF),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Function) => Color.FromRgb(0xF2, 0xC8, 0x55),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Property) => Color.FromRgb(0x67, 0xBE, 0xFF),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Preprocessor) => Color.FromRgb(0xF1, 0x96, 0xAF),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, true, SyntaxTokenKind.Punctuation) => Color.FromRgb(0x92, 0x97, 0x9E),
            (SyntaxLanguage.Python, true, SyntaxTokenKind.Keyword) => Color.FromRgb(0xC8, 0x8B, 0xFF),
            (SyntaxLanguage.Python, true, SyntaxTokenKind.Type) => Color.FromRgb(0x67, 0xBE, 0xFF),
            (SyntaxLanguage.Python, true, SyntaxTokenKind.Function) => Color.FromRgb(0xF2, 0xC8, 0x55),
            (_, true, SyntaxTokenKind.Comment) => Color.FromRgb(0x6F, 0x86, 0x77),
            (_, true, SyntaxTokenKind.Keyword) => Color.FromRgb(0x7F, 0xB4, 0xFF),
            (_, true, SyntaxTokenKind.Type) => Color.FromRgb(0x83, 0xD2, 0xC7),
            (_, true, SyntaxTokenKind.String) => Color.FromRgb(0xE4, 0xC0, 0x75),
            (_, true, SyntaxTokenKind.Number) => Color.FromRgb(0xC7, 0xA0, 0xFF),
            (_, true, SyntaxTokenKind.Function) => Color.FromRgb(0xD8, 0xDD, 0xE3),
            (_, true, SyntaxTokenKind.Property) => Color.FromRgb(0x82, 0xC9, 0xFF),
            (_, true, SyntaxTokenKind.Tag) => Color.FromRgb(0x91, 0xB6, 0xFF),
            (_, true, SyntaxTokenKind.Attribute) => Color.FromRgb(0x8D, 0xD9, 0xC4),
            (_, true, SyntaxTokenKind.Preprocessor) => Color.FromRgb(0xF1, 0x96, 0xAF),
            (_, true, SyntaxTokenKind.Punctuation) => Color.FromRgb(0xA9, 0xB0, 0xB8),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, false, SyntaxTokenKind.Keyword) => Color.FromRgb(0x8D, 0x4E, 0xC4),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, false, SyntaxTokenKind.Type) => Color.FromRgb(0x0F, 0x75, 0xC7),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, false, SyntaxTokenKind.Function) => Color.FromRgb(0xB1, 0x73, 0x00),
            (SyntaxLanguage.Cpp or SyntaxLanguage.CSharp, false, SyntaxTokenKind.Property) => Color.FromRgb(0x0F, 0x75, 0xC7),
            (SyntaxLanguage.Python, false, SyntaxTokenKind.Keyword) => Color.FromRgb(0x8D, 0x4E, 0xC4),
            (SyntaxLanguage.Python, false, SyntaxTokenKind.Type) => Color.FromRgb(0x0F, 0x75, 0xC7),
            (SyntaxLanguage.Python, false, SyntaxTokenKind.Function) => Color.FromRgb(0xB1, 0x73, 0x00),
            (_, false, SyntaxTokenKind.Comment) => Color.FromRgb(0x5B, 0x73, 0x61),
            (_, false, SyntaxTokenKind.Keyword) => Color.FromRgb(0x0E, 0x66, 0xBB),
            (_, false, SyntaxTokenKind.Type) => Color.FromRgb(0x0D, 0x86, 0x75),
            (_, false, SyntaxTokenKind.String) => Color.FromRgb(0x9A, 0x5C, 0x13),
            (_, false, SyntaxTokenKind.Number) => Color.FromRgb(0x7A, 0x4D, 0xB2),
            (_, false, SyntaxTokenKind.Function) => Color.FromRgb(0x3D, 0x46, 0x4F),
            (_, false, SyntaxTokenKind.Property) => Color.FromRgb(0x0F, 0x75, 0xC7),
            (_, false, SyntaxTokenKind.Tag) => Color.FromRgb(0x2D, 0x67, 0xC4),
            (_, false, SyntaxTokenKind.Attribute) => Color.FromRgb(0x0D, 0x86, 0x75),
            (_, false, SyntaxTokenKind.Preprocessor) => Color.FromRgb(0xB5, 0x43, 0x66),
            (_, false, SyntaxTokenKind.Punctuation) => Color.FromRgb(0x6B, 0x72, 0x78),
            _ => IsDarkTheme ? Color.FromRgb(0xE9, 0xE9, 0xE9) : Color.FromRgb(0x20, 0x20, 0x20),
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool IsOccupied(IEnumerable<SyntaxToken> tokens, int index) => tokens.Any(token => index >= token.Start && index < token.Start + token.Length);

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value is '_' or '$';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';

    private static HashSet<string> CreateWordSet(params string[] values) => values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly record struct SyntaxToken(int Start, int Length, SyntaxTokenKind Kind);

    private enum SyntaxTokenKind
    {
        Comment,
        Keyword,
        Type,
        String,
        Number,
        Function,
        Property,
        Tag,
        Attribute,
        Preprocessor,
        Punctuation,
    }
}
