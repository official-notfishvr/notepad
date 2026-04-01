using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FastNote.App;

internal sealed class SpellCheckColorizer : IBackgroundRenderer, IDisposable
{
    private const int CacheLimit = 1024;

    private readonly WindowsSpellCheckService? _spellCheckService = WindowsSpellCheckService.TryCreate();
    private readonly Dictionary<string, SpellErrorSpan[]> _cache = new(StringComparer.Ordinal);
    private readonly Pen _underlinePen;

    public SpellCheckColorizer()
    {
        _underlinePen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), 1.2);
        _underlinePen.Freeze();
    }

    public bool IsEnabled { get; set; }

    public bool IsAvailable => _spellCheckService is not null;

    public KnownLayer Layer => KnownLayer.Selection;

    public void ClearCache()
    {
        _cache.Clear();
    }

    public bool TryGetSpellingIssue(TextDocument document, int offset, out SpellingIssue issue)
    {
        issue = default;
        if (!IsEnabled || _spellCheckService is null || document.TextLength == 0)
        {
            return false;
        }

        var safeOffset = Math.Clamp(offset, 0, Math.Max(0, document.TextLength - 1));
        var line = document.GetLineByOffset(safeOffset);
        var lineText = document.GetText(line);
        if (string.IsNullOrWhiteSpace(lineText))
        {
            return false;
        }

        foreach (var error in GetErrors(lineText))
        {
            var startOffset = line.Offset + error.Start;
            var endOffset = startOffset + error.Length;
            if (safeOffset < startOffset || safeOffset > endOffset)
            {
                continue;
            }

            var word = document.GetText(startOffset, error.Length);
            issue = new SpellingIssue(startOffset, error.Length, word, _spellCheckService.Suggest(word));
            return true;
        }

        return false;
    }

    public void AddWordToDictionary(string word)
    {
        _spellCheckService?.Add(word);
        ClearCache();
    }

    public void IgnoreWord(string word)
    {
        _spellCheckService?.Ignore(word);
        ClearCache();
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!IsEnabled || _spellCheckService is null || !textView.VisualLinesValid)
        {
            return;
        }

        var document = textView.Document;
        if (document is null)
        {
            return;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var line = visualLine.FirstDocumentLine;
            var lineText = document.GetText(line);
            if (string.IsNullOrWhiteSpace(lineText) || lineText.Length > 4096)
            {
                continue;
            }

            foreach (var error in GetErrors(lineText))
            {
                var segment = new SpellSegment(line.Offset + error.Start, error.Length);
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, true))
                {
                    DrawWavyUnderline(drawingContext, rect);
                }
            }
        }
    }

    public void Dispose()
    {
        _spellCheckService?.Dispose();
    }

    private void DrawWavyUnderline(DrawingContext drawingContext, Rect rect)
    {
        if (rect.Width <= 1 || rect.Height <= 1)
        {
            return;
        }

        var baseline = rect.Bottom - 1;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();

        var x = rect.Left;
        var y = baseline;
        var step = 3.0;
        var up = true;
        context.BeginFigure(new Point(x, y), false, false);

        while (x < rect.Right)
        {
            x = Math.Min(rect.Right, x + step);
            y = up ? baseline - 2 : baseline;
            context.LineTo(new Point(x, y), true, false);
            up = !up;
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, _underlinePen, geometry);
    }

    private SpellErrorSpan[] GetErrors(string lineText)
    {
        if (_cache.TryGetValue(lineText, out var cachedErrors))
        {
            return cachedErrors;
        }

        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        var errors = _spellCheckService?.CheckLine(lineText) ?? [];
        _cache[lineText] = errors;
        return errors;
    }

    private readonly record struct SpellErrorSpan(int Start, int Length);

    private readonly record struct SpellSegment(int Offset, int Length) : ISegment
    {
        public int EndOffset => Offset + Length;
    }

    internal readonly record struct SpellingIssue(int Start, int Length, string Word, IReadOnlyList<string> Suggestions);

    private sealed class WindowsSpellCheckService : IDisposable
    {
        private readonly ISpellCheckerFactory _factory;
        private readonly ISpellChecker _spellChecker;
        private bool _disposed;

        private WindowsSpellCheckService(ISpellCheckerFactory factory, ISpellChecker spellChecker)
        {
            _factory = factory;
            _spellChecker = spellChecker;
        }

        public static WindowsSpellCheckService? TryCreate()
        {
            ISpellCheckerFactory? factory = null;

            try
            {
                factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();

                foreach (var languageTag in GetPreferredLanguageTags())
                {
                    if (!factory.IsSupported(languageTag))
                    {
                        continue;
                    }

                    var spellChecker = factory.CreateSpellChecker(languageTag);
                    return new WindowsSpellCheckService(factory, spellChecker);
                }
            }
            catch { }

            if (factory is not null)
            {
                Marshal.FinalReleaseComObject(factory);
            }

            return null;
        }

        public SpellErrorSpan[] CheckLine(string text)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            IEnumSpellingError? errorEnumerator = null;
            var errors = new List<SpellErrorSpan>();

            try
            {
                errorEnumerator = _spellChecker.ComprehensiveCheck(text);

                while (true)
                {
                    var spellingError = errorEnumerator.Next();
                    if (spellingError is null)
                    {
                        break;
                    }

                    try
                    {
                        var start = checked((int)spellingError.StartIndex);
                        var length = checked((int)spellingError.Length);
                        if (ShouldHighlight(text, start, length))
                        {
                            errors.Add(new SpellErrorSpan(start, length));
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(spellingError);
                    }
                }
            }
            catch
            {
                return [];
            }
            finally
            {
                if (errorEnumerator is not null)
                {
                    Marshal.FinalReleaseComObject(errorEnumerator);
                }
            }

            return [.. errors];
        }

        public IReadOnlyList<string> Suggest(string word)
        {
            if (_disposed || string.IsNullOrWhiteSpace(word))
            {
                return [];
            }

            IEnumString? suggestions = null;
            try
            {
                suggestions = _spellChecker.Suggest(word);
                return EnumerateStrings(suggestions);
            }
            catch
            {
                return [];
            }
            finally
            {
                if (suggestions is not null)
                {
                    Marshal.FinalReleaseComObject(suggestions);
                }
            }
        }

        public void Add(string word)
        {
            if (_disposed || string.IsNullOrWhiteSpace(word))
            {
                return;
            }

            try
            {
                _spellChecker.Add(word);
            }
            catch { }
        }

        public void Ignore(string word)
        {
            if (_disposed || string.IsNullOrWhiteSpace(word))
            {
                return;
            }

            try
            {
                _spellChecker.Ignore(word);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Marshal.FinalReleaseComObject(_spellChecker);
            Marshal.FinalReleaseComObject(_factory);
        }

        private static IReadOnlyList<string> EnumerateStrings(IEnumString values)
        {
            var results = new List<string>();
            var items = new string[1];

            while (values.Next(1, items, IntPtr.Zero) == 0)
            {
                var value = items[0];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value);
                }

                items[0] = string.Empty;
            }

            return results;
        }

        private static IEnumerable<string> GetPreferredLanguageTags()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var culture in new[] { CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture })
            {
                for (var current = culture; current is not null && current != CultureInfo.InvariantCulture; current = current.Parent)
                {
                    if (!string.IsNullOrWhiteSpace(current.Name) && seen.Add(current.Name))
                    {
                        yield return current.Name;
                    }
                }
            }

            if (seen.Add("en-US"))
            {
                yield return "en-US";
            }

            if (seen.Add("en"))
            {
                yield return "en";
            }
        }

        private static bool ShouldHighlight(string text, int start, int length)
        {
            if (start < 0 || length < 2 || start + length > text.Length)
            {
                return false;
            }

            var token = text.Substring(start, length);
            if (!token.Any(char.IsLetter))
            {
                return false;
            }

            if (token.All(char.IsUpper))
            {
                return false;
            }

            foreach (var ch in token)
            {
                if (char.IsDigit(ch) || ch is '_' or '/' or '\\' or '@' or ':' or '.' or '#' or '=')
                {
                    return false;
                }
            }

            return !Uri.IsWellFormedUriString(token, UriKind.Absolute);
        }

        [ComImport]
        [Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellCheckerFactory
        {
            IEnumString SupportedLanguages { get; }

            [return: MarshalAs(UnmanagedType.Bool)]
            bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

            ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        }

        [ComImport]
        [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellChecker
        {
            string LanguageTag { get; }

            IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

            IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);

            void Add([MarshalAs(UnmanagedType.LPWStr)] string word);

            void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);

            void AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);

            byte GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId);

            IEnumString OptionIds { get; }

            string Id { get; }

            string LocalizedName { get; }

            void add_SpellCheckerChanged(nint handler, out uint eventCookie);

            void remove_SpellCheckerChanged(uint eventCookie);

            nint GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId);

            IEnumSpellingError ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text);
        }

        [ComImport]
        [Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumSpellingError
        {
            ISpellingError? Next();
        }

        [ComImport]
        [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellingError
        {
            uint StartIndex { get; }
            uint Length { get; }
            CorrectiveAction CorrectiveAction { get; }

            string? Replacement { get; }
        }

        private enum CorrectiveAction
        {
            None = 0,
            GetSuggestions = 1,
            Replace = 2,
            Delete = 3,
        }

        [ComImport]
        [Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
        private class SpellCheckerFactoryClass;
    }
}
