using System.Text.RegularExpressions;
using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.Core.Emmet;

/// <summary>How expansions are written: the indentation unit for nested lines.</summary>
public sealed record EmmetOptions(string Indent = "\t");

/// <summary>An abbreviation found before the caret: where it starts on the line, and its text.</summary>
public readonly record struct EmmetAbbreviation(int Start, string Text);

/// <summary>
/// Emmet for markup, built in: expands abbreviations such as <c>ul>li.item$*3</c> or <c>!</c> into
/// HTML in snippet syntax (tab stops where text goes), finds the abbreviation before the caret, and
/// offers completion items for it the way VS Code does: the expanded abbreviation first, then the
/// snippet names it is a prefix of (<c>inp</c> → <c>input</c>, <c>input:checkbox</c>…).
/// </summary>
public static partial class EmmetEngine
{
    public const string ItemDetail = "Emmet Abbreviation";

    private const int MaxSuggestions = 60;

    private static readonly System.Buffers.SearchValues<char> Operators = System.Buffers.SearchValues.Create(".#>+^*[{(/");

    /// <summary>The expansion in LSP snippet syntax, or null when the text is not a valid abbreviation.</summary>
    public static string? Expand(string abbreviation, EmmetOptions? options = null) =>
        EmmetExpander.Expand(abbreviation, options ?? new EmmetOptions());

    /// <summary>
    /// True when <paramref name="abbreviation"/> is worth offering or expanding on Tab: it parses, and it
    /// either uses Emmet syntax (<c>.</c> <c>#</c> <c>&gt;</c> <c>*</c>…) or names an HTML element or a snippet,
    /// so plain words in prose ("hello") are left alone.
    /// </summary>
    public static bool IsValid(string abbreviation)
    {
        if (abbreviation.Length == 0 || Expand(abbreviation) is null)
        {
            return false;
        }

        return abbreviation.AsSpan().IndexOfAny(Operators) >= 0
               || EmmetSnippets.Html.ContainsKey(abbreviation)
               || EmmetSnippets.Tags.Contains(abbreviation);
    }

    /// <summary>
    /// The abbreviation that ends at the end of <paramref name="line"/> (the text before the caret): the
    /// longest run of abbreviation characters, with <c>[…]</c> and <c>{…}</c> taken whole (spaces and
    /// quotes included) and stopping after a tag (<c>&lt;div&gt;</c><c>ul&gt;li</c> gives <c>ul&gt;li</c>).
    /// </summary>
    public static EmmetAbbreviation? Extract(string line)
    {
        var closers = new Stack<char>();
        var start = line.Length;
        while (start > 0)
        {
            var c = line[start - 1];
            if (closers.Count > 0 && closers.Peek() is '[' or '{')
            {
                // Inside attributes or text: anything goes until the matching opener.
                if (c == closers.Peek())
                {
                    closers.Pop();
                }
                else if (c is ']' or '}')
                {
                    closers.Push(c == ']' ? '[' : '{');
                }

                start--;
                continue;
            }

            if (c is ']' or '}' or ')')
            {
                closers.Push(c switch { ']' => '[', '}' => '{', _ => '(' });
            }
            else if (c is '[' or '{' or '(')
            {
                if (closers.Count == 0 || closers.Peek() != c)
                {
                    break; // an unclosed bracket: the caret is inside it
                }

                closers.Pop();
            }
            else if (c == '>' && EndsTag(line, start - 1))
            {
                break;
            }
            else if (!IsAbbreviationChar(c))
            {
                break;
            }

            start--;
        }

        if (closers.Count > 0)
        {
            return null;
        }

        // Operators cannot start an abbreviation ("</p>" leaves ">" when the tag check missed it).
        while (start < line.Length && line[start] is '>' or '+' or '^' or '*' or ')')
        {
            start++;
        }

        return start < line.Length ? new EmmetAbbreviation(start, line[start..]) : null;
    }

    /// <summary>
    /// True when text typed after <paramref name="textBefore"/> would be element content: not inside a
    /// tag (or its attribute values), a comment, or a <c>&lt;script&gt;</c> / <c>&lt;style&gt;</c> block.
    /// </summary>
    public static bool IsMarkupContext(string textBefore)
    {
        var tagOpen = textBefore.LastIndexOf('<');
        if (tagOpen >= 0 && tagOpen > textBefore.LastIndexOf('>') && tagOpen + 1 < textBefore.Length
            && (char.IsAsciiLetter(textBefore[tagOpen + 1]) || textBefore[tagOpen + 1] is '/' or '!'))
        {
            return false; // in "<div class=…"
        }

        if (textBefore.LastIndexOf("<!--", StringComparison.Ordinal) > textBefore.LastIndexOf("-->", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var block in (ReadOnlySpan<string>)["script", "style"])
        {
            var open = LastTag(textBefore, "<" + block);
            if (open >= 0 && open > LastTag(textBefore, "</" + block))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The expansion as shown in the details card: tab stops as <c>|</c>, placeholders as their text.</summary>
    public static string Preview(string snippet, string? indent = null)
    {
        var expansion = Snippet.Expand(snippet);
        var text = expansion.Text;
        foreach (var stop in expansion.TabStops.Where(s => s.Length == 0).Select(s => s.Start).Distinct().OrderDescending())
        {
            text = text.Insert(stop, "|");
        }

        return indent is null ? text : text.Replace("\t", indent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Completion items for the abbreviation before the caret: <paramref name="textBefore"/> is the
    /// document up to the caret (or a long enough tail of it), <paramref name="caret"/> its position.
    /// <see cref="EmmetCompletions.Abbreviation"/> is the typed abbreviation's own item, when valid.
    /// </summary>
    public static EmmetCompletions Complete(string textBefore, TextPosition caret, EmmetOptions? options = null)
    {
        options ??= new EmmetOptions();
        var line = textBefore[(textBefore.LastIndexOf('\n') + 1)..];
        if (Extract(line) is not { } found || !IsMarkupContext(textBefore[..^found.Text.Length]))
        {
            return EmmetCompletions.None;
        }

        var range = new TextRange(caret with { Column = caret.Column - found.Text.Length }, caret);
        CompletionItem? Item(string label, string abbreviation, string sortText) =>
            Expand(abbreviation, options) is { } snippet
                ? new CompletionItem
                {
                    Label = label,
                    Kind = CompletionItemKind.Snippet,
                    Detail = ItemDetail,
                    Documentation = Preview(snippet),
                    FilterText = label,
                    SortText = sortText,
                    InsertText = snippet,
                    IsSnippet = true,
                    TextEdit = new TextReplacement(range, snippet),
                }
                : null;

        var abbreviationItem = IsValid(found.Text) ? Item(found.Text, found.Text, "!") : null;
        var suggestions = new List<CompletionItem>();
        if (PlainName().IsMatch(found.Text))
        {
            var names = EmmetSnippets.Html.Keys.Concat(EmmetSnippets.Tags)
                .Where(name => name.Length > found.Text.Length && name.StartsWith(found.Text, StringComparison.Ordinal))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .Take(MaxSuggestions);
            suggestions.AddRange(names.Select(name => Item(name, name, "!" + name)).OfType<CompletionItem>());
        }

        return new EmmetCompletions(abbreviationItem, abbreviationItem is null ? suggestions : [abbreviationItem, .. suggestions]);
    }

    private static bool IsAbbreviationChar(char c) =>
        EmmetParser.IsNameChar(c) || c is '.' or '#' or '>' or '+' or '^' or '*' or '/' or '%';

    /// <summary>True when the <c>&gt;</c> at <paramref name="index"/> closes a tag such as <c>&lt;div class="a"&gt;</c>.</summary>
    private static bool EndsTag(string line, int index)
    {
        var open = line.LastIndexOf('<', index);
        return open >= 0 && TagPattern().IsMatch(line.AsSpan(open, index - open + 1));
    }

    private static int LastTag(string text, string tag)
    {
        var index = text.Length;
        while ((index = index == 0 ? -1 : text.LastIndexOf(tag, index - 1, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var after = index + tag.Length;
            if (after >= text.Length || !char.IsAsciiLetterOrDigit(text[after]))
            {
                return index; // "<script" but not "<scripts"
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^</?[A-Za-z][\w:.-]*(\s[^<>]*)?/?>$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z!][\w:!+-]*$")]
    private static partial Regex PlainName();
}

/// <summary>Emmet's completion items, the typed abbreviation's own item (if any) first.</summary>
public sealed record EmmetCompletions(CompletionItem? Abbreviation, IReadOnlyList<CompletionItem> Items)
{
    public static readonly EmmetCompletions None = new(null, []);
}
