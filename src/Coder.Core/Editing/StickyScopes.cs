using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// A block of code: its header (<see cref="StartLine"/>, e.g. <c>class Foo</c> or <c>if (x) {</c>) and the
/// last line that still belongs to it (<see cref="EndLine"/>, the line before a closing <c>}</c>).
/// </summary>
public readonly record struct ScopeRange(int StartLine, int EndLine);

/// <summary>
/// Finds the scopes for the editor's sticky scroll: the header lines of the blocks enclosing the top of
/// the viewport stay pinned there, stacked by nesting. Brace languages (C#, TypeScript, JSON…) use their
/// <c>{ }</c> and <c>[ ]</c> (skipping strings and comments); others (HTML, Python, YAML…) use indentation.
/// The language server's document symbols, when there are any, are merged in.
/// </summary>
public static class StickyScopes
{
    /// <summary>The scopes of a document, ready to query.</summary>
    public static ScopeIndex Compute(TextDocument document, LanguageDefinition language, int tabSize, IReadOnlyList<ScopeRange>? symbolScopes = null)
    {
        var lines = new string[document.LineCount];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = document.GetLineText(i);
        }

        var found = UsesBraces(language) ? FromBrackets(lines, language) : FromIndentation(lines, tabSize);
        return new ScopeIndex(symbolScopes is { Count: > 0 } ? [.. found, .. symbolScopes] : found);
    }

    /// <summary>
    /// The regions a document can fold, like VS Code's: blocks between brackets (folded from the line of the
    /// opening bracket, so a lone "{" keeps its own chevron), or by indentation for other languages. Sorted,
    /// one per first line (the widest).
    /// </summary>
    public static IReadOnlyList<ScopeRange> FoldRanges(TextDocument document, LanguageDefinition language, int tabSize)
    {
        var lines = new string[document.LineCount];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = document.GetLineText(i);
        }

        var found = UsesBraces(language) ? FromBrackets(lines, language, liftHeaders: false) : FromIndentation(lines, tabSize);
        return [.. found.GroupBy(r => r.StartLine).Select(g => g.MaxBy(r => r.EndLine)).OrderBy(r => r.StartLine)];
    }

    /// <summary>Whether <paramref name="language"/>'s blocks are delimited by braces (rather than indentation or tags).</summary>
    public static bool UsesBraces(LanguageDefinition language) =>
        !language.MarkupTags && language.Brackets.Contains('{', StringComparison.Ordinal) && !IndentedLanguages.Contains(language.Id);

    /// <summary>Languages with braces whose blocks are still shown by indentation (or keywords such as <c>end</c>).</summary>
    private static readonly HashSet<string> IndentedLanguages =
        ["python", "yaml", "toml", "ruby", "lua", "sql", "shellscript", "dockerfile", "markdown", "plaintext"];

    /// <summary>
    /// Scopes from the language server's symbols: a symbol's header is the line of its name, and it ends on
    /// the line before its closing bracket (or on its last line when that line has more than a bracket).
    /// </summary>
    public static IReadOnlyList<ScopeRange> FromSymbols(IEnumerable<DocumentSymbol> symbols, int lineCount, Func<int, string> lineText)
    {
        var result = new List<ScopeRange>();
        Visit(symbols);
        return result;

        void Visit(IEnumerable<DocumentSymbol> level)
        {
            foreach (var symbol in level)
            {
                var start = symbol.SelectionRange.Start.Line;
                var end = Math.Min(symbol.Range.End.Line, lineCount - 1); // symbols may be from a slightly older text
                if (end > start && IsClosingLine(lineText(end)))
                {
                    end--;
                }

                if (end > start)
                {
                    result.Add(new ScopeRange(start, end));
                }

                Visit(symbol.Children);
            }
        }
    }

    /// <summary>Blocks between matching <c>{ }</c> and <c>[ ]</c>, ignoring brackets in strings and comments.</summary>
    public static IReadOnlyList<ScopeRange> FromBrackets(IReadOnlyList<string> lines, LanguageDefinition language, bool liftHeaders = true)
    {
        var result = new List<ScopeRange>();
        var open = new Stack<(char Bracket, int Line)>();
        var lineComment = language.LineComment;
        var block = language.BlockComment;
        var multiLine = language.MultiLineStringDelimiter;
        var quotes = language.StringQuotes;

        string? closeMultiLine = null; // inside a block comment or a multi-line string: what ends it
        for (var line = 0; line < lines.Count; line++)
        {
            var text = lines[line];
            var i = 0;
            while (i < text.Length)
            {
                if (closeMultiLine is not null)
                {
                    var end = text.IndexOf(closeMultiLine, i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    i = end + closeMultiLine.Length;
                    closeMultiLine = null;
                    continue;
                }

                var c = text[i];
                if (lineComment is not null && At(text, i, lineComment))
                {
                    break;
                }

                if (block is { } comment && At(text, i, comment.Open))
                {
                    closeMultiLine = comment.Close;
                    i += comment.Open.Length;
                    continue;
                }

                if (multiLine is not null && At(text, i, multiLine))
                {
                    closeMultiLine = multiLine;
                    i += multiLine.Length;
                    continue;
                }

                if (quotes.Contains(c, StringComparison.Ordinal))
                {
                    i = SkipString(text, i);
                    continue;
                }

                if (c is '{' or '[')
                {
                    open.Push((c, line));
                }
                else if (c is '}' or ']')
                {
                    var expected = c == '}' ? '{' : '[';
                    // A stray closer (unbalanced code being typed) closes nothing rather than everything.
                    if (open.Count > 0 && open.Peek().Bracket == expected)
                    {
                        Add(open.Pop().Line, line, closerFirst: text.AsSpan(0, i).IsWhiteSpace());
                    }
                }

                i++;
            }
        }

        return result;

        void Add(int openLine, int closeLine, bool closerFirst)
        {
            var end = closerFirst ? closeLine - 1 : closeLine;
            if (end <= openLine)
            {
                return;
            }

            // "{" alone on its line (Allman style, as in C#): the header is the line above it.
            var header = openLine;
            if (liftHeaders && lines[openLine].AsSpan().Trim() is "{" or "[" && PreviousNonBlank(lines, openLine) is { } above
                && lines[above].AsSpan().TrimEnd() is var previous && previous.Length > 0 && previous[^1] is not (';' or '{' or '}' or ',' or '[' or '('))
            {
                header = above;
            }

            result.Add(new ScopeRange(header, end));
        }
    }

    /// <summary>
    /// Blocks by indentation, like VS Code's indentation folding: a line whose next non-blank line is
    /// indented deeper starts a block that runs to the last line indented deeper than it.
    /// </summary>
    public static IReadOnlyList<ScopeRange> FromIndentation(IReadOnlyList<string> lines, int tabSize)
    {
        var result = new List<ScopeRange>();
        var open = new Stack<(int Line, int Indent)>();
        var lastNonBlank = -1;
        for (var line = 0; line < lines.Count; line++)
        {
            var text = lines[line];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var indent = TextColumns.ToVisual(text, TextColumns.LeadingWhitespaceLength(text), tabSize);
            while (open.Count > 0 && open.Peek().Indent >= indent)
            {
                Close(open.Pop().Line);
            }

            open.Push((line, indent));
            lastNonBlank = line;
        }

        while (open.Count > 0)
        {
            Close(open.Pop().Line);
        }

        return result;

        void Close(int start)
        {
            if (lastNonBlank > start)
            {
                result.Add(new ScopeRange(start, lastNonBlank));
            }
        }
    }

    private static bool IsClosingLine(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '}' or ']' or ')';
    }

    private static int? PreviousNonBlank(IReadOnlyList<string> lines, int line)
    {
        for (var i = line - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                return i;
            }
        }

        return null;
    }

    private static bool At(string text, int index, string token) =>
        token.Length > 0 && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    /// <summary>Index just past the string starting at <paramref name="start"/> (or the line's end).</summary>
    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i + 1;
            }
        }

        return text.Length;
    }
}

/// <summary>
/// Scopes sorted for fast lookup: finding the blocks around a line walks up from the nearest block
/// before it, so a query costs a binary search plus the nesting depth, whatever the file's size.
/// </summary>
public sealed class ScopeIndex
{
    public static ScopeIndex Empty { get; } = new([]);

    private readonly ScopeRange[] _ranges;
    private readonly int[] _parents;

    public ScopeIndex(IEnumerable<ScopeRange> ranges)
    {
        // One scope per header line (the widest), outer before inner.
        _ranges = ranges
            .Where(r => r.EndLine > r.StartLine)
            .GroupBy(r => r.StartLine)
            .Select(g => new ScopeRange(g.Key, g.Max(r => r.EndLine)))
            .OrderBy(r => r.StartLine)
            .ToArray();

        _parents = new int[_ranges.Length];
        var stack = new Stack<int>();
        for (var i = 0; i < _ranges.Length; i++)
        {
            while (stack.Count > 0 && _ranges[stack.Peek()].EndLine < _ranges[i].EndLine)
            {
                stack.Pop(); // ended before this one ends: not an ancestor
            }

            _parents[i] = stack.Count > 0 ? stack.Peek() : -1;
            stack.Push(i);
        }
    }

    public IReadOnlyList<ScopeRange> Ranges => _ranges;

    /// <summary>The scopes whose body contains <paramref name="line"/> (header above it), outermost first.</summary>
    public List<ScopeRange> Enclosing(int line)
    {
        var result = new List<ScopeRange>();

        // The last scope whose header is above the line; every enclosing scope is it or an ancestor of it.
        int low = 0, high = _ranges.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_ranges[mid].StartLine < line)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (var i = low - 1; i >= 0; i = _parents[i])
        {
            if (_ranges[i].EndLine >= line)
            {
                result.Add(_ranges[i]);
            }
        }

        result.Reverse();
        return result;
    }

    /// <summary>
    /// The scopes to pin when <paramref name="topLine"/> is the first visible line: each pinned header
    /// covers a line, so the next level is decided by the line just below the headers so far.
    /// </summary>
    public List<ScopeRange> StickyHeaders(int topLine, int maxCount)
    {
        var result = new List<ScopeRange>();
        while (result.Count < maxCount)
        {
            var chain = Enclosing(topLine + result.Count);
            if (chain.Count <= result.Count || !chain.Take(result.Count).SequenceEqual(result))
            {
                break;
            }

            result.Add(chain[result.Count]);
        }

        return result;
    }
}
