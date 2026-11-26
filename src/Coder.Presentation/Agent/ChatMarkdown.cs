using System.Text;

namespace Coder.Presentation.Agent;

public enum ChatBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Quote,
    Code,
    Table,
    Rule,
}

public enum InlineKind
{
    Text,
    Bold,
    Italic,
    BoldItalic,
    Code,
    Link,
    Strike,
}

/// <summary>A run of text with one style; links keep their address.</summary>
public sealed record InlineSpan(InlineKind Kind, string Text, string? Url = null);

/// <summary>
/// One block of a chat reply. Paragraphs, headings, list items and quotes have <see cref="Spans"/>;
/// code has <see cref="Text"/> and <see cref="Language"/>; tables have <see cref="Rows"/> (the first is the header).
/// </summary>
public sealed record ChatBlock(ChatBlockKind Kind)
{
    public IReadOnlyList<InlineSpan> Spans { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public string? Language { get; init; }

    /// <summary>Heading level (1–6), or a list item's nesting depth (0 for top level).</summary>
    public int Level { get; init; }

    /// <summary>A list item's marker: "•" or its number ("3.").</summary>
    public string? Marker { get; init; }

    /// <summary>A code block whose closing fence has not arrived yet (the reply is still streaming).</summary>
    public bool IsOpen { get; init; }

    public IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineSpan>>> Rows { get; init; } = [];

    /// <summary>The block's plain text (for copying and tests).</summary>
    public string PlainText => Kind == ChatBlockKind.Code ? Text : string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// The Markdown chat models write, read into blocks the chat draws: headings, paragraphs, bulleted and
/// numbered lists (nested by indent), quotes, fenced code, tables and rules, with bold, italic, code,
/// links and strikethrough inside. Tolerant of half-written input, since it is re-read as a reply streams.
/// </summary>
public static class ChatMarkdown
{
    public static IReadOnlyList<ChatBlock> Parse(string markdown)
    {
        var blocks = new List<ChatBlock>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new ChatBlock(ChatBlockKind.Paragraph) { Spans = Inline(paragraph.ToString()) });
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var indent = line.Length - line.TrimStart().Length;

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                var closed = false;
                for (i++; i < lines.Length; i++)
                {
                    if (lines[i].Trim().StartsWith(fence, StringComparison.Ordinal) && lines[i].Trim().Trim(fence[0]).Length == 0)
                    {
                        closed = true;
                        break;
                    }

                    code.Add(indent > 0 && lines[i].Length >= indent && lines[i][..indent].Trim().Length == 0 ? lines[i][indent..] : lines[i]);
                }

                blocks.Add(new ChatBlock(ChatBlockKind.Code) { Text = string.Join('\n', code), Language = language.Length > 0 ? language.Split(' ')[0] : null, IsOpen = !closed });
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (IsRule(trimmed))
            {
                Flush();
                blocks.Add(new ChatBlock(ChatBlockKind.Rule));
                continue;
            }

            if (trimmed[0] == '#')
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && (trimmed.Length == level || trimmed[level] == ' '))
                {
                    Flush();
                    blocks.Add(new ChatBlock(ChatBlockKind.Heading) { Level = level, Spans = Inline(trimmed[level..].Trim().TrimEnd('#').Trim()) });
                    continue;
                }
            }

            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quote = new StringBuilder(trimmed.TrimStart('>').Trim());
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('>'))
                {
                    quote.Append(' ').Append(lines[++i].Trim().TrimStart('>').Trim());
                }

                blocks.Add(new ChatBlock(ChatBlockKind.Quote) { Spans = Inline(quote.ToString()) });
                continue;
            }

            if (ListMarker(trimmed) is { } marker)
            {
                Flush();
                var item = new StringBuilder(trimmed[marker.Length..].Trim());

                // Continuation lines (indented, not a new item or block) belong to the item.
                while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent
                    && ListMarker(lines[i + 1].Trim()) is null && !lines[i + 1].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    item.Append(' ').Append(lines[++i].Trim());
                }

                var number = marker.TrimEnd();
                blocks.Add(new ChatBlock(ChatBlockKind.ListItem)
                {
                    Level = Math.Min(indent / 2, 6),
                    Marker = number is "-" or "*" or "+" ? "•" : number.Replace(')', '.'),
                    Spans = Inline(item.ToString()),
                });
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableDivider(lines[i + 1].Trim()))
            {
                Flush();
                var rows = new List<IReadOnlyList<IReadOnlyList<InlineSpan>>> { Cells(trimmed) };
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    rows.Add(Cells(lines[i].Trim()));
                }

                i--;
                blocks.Add(new ChatBlock(ChatBlockKind.Table) { Rows = rows });
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(line.EndsWith("  ", StringComparison.Ordinal) ? '\n' : ' ');
            }

            paragraph.Append(trimmed);
        }

        Flush();
        return blocks;
    }

    private static bool IsRule(string trimmed) =>
        trimmed.Length >= 3 && (trimmed.All(c => c is '-' or ' ') || trimmed.All(c => c is '*' or ' ') || trimmed.All(c => c is '_' or ' '))
        && trimmed.Count(c => c != ' ') >= 3;

    /// <summary>"- ", "* ", "+ ", "1. " or "1) " at the start of a line, or null.</summary>
    private static string? ListMarker(string trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            return trimmed[..2];
        }

        var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
        return digits is > 0 and < 4 && trimmed.Length > digits + 1 && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' '
            ? trimmed[..(digits + 2)]
            : null;
    }

    private static bool IsTableDivider(string trimmed) =>
        trimmed.StartsWith('|') && trimmed.Contains('-', StringComparison.Ordinal) && trimmed.All(c => c is '|' or '-' or ':' or ' ');

    private static List<IReadOnlyList<InlineSpan>> Cells(string row)
    {
        var inner = row.Trim().Trim('|');
        return inner.Split('|').Select(cell => (IReadOnlyList<InlineSpan>)Inline(cell.Trim())).ToList();
    }

    /// <summary>Splits text into styled runs: <c>`code`</c>, <c>**bold**</c>, <c>*italic*</c>, <c>***both***</c>, <c>~~strike~~</c>, <c>[text](url)</c>.</summary>
    public static IReadOnlyList<InlineSpan> Inline(string text)
    {
        var spans = new List<InlineSpan>();
        var plain = new StringBuilder();
        void Add(InlineKind kind, string value, string? url = null)
        {
            if (plain.Length > 0)
            {
                spans.Add(new InlineSpan(InlineKind.Text, plain.ToString()));
                plain.Clear();
            }

            if (value.Length > 0)
            {
                spans.Add(new InlineSpan(kind, value, url));
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_{}[]()#+-.!|~<>".Contains(text[i + 1], StringComparison.Ordinal))
            {
                plain.Append(text[++i]);
                continue;
            }

            if (c == '`')
            {
                var ticks = text.AsSpan(i).IndexOfAnyExcept('`');
                ticks = ticks < 0 ? text.Length - i : ticks;
                var fence = new string('`', ticks);
                var end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > 0)
                {
                    Add(InlineKind.Code, text[(i + ticks)..end].Trim());
                    i = end + ticks - 1;
                    continue;
                }
            }

            if (c == '[' && LinkAt(text, i) is { } link)
            {
                Add(InlineKind.Link, link.Text, link.Url);
                i = link.End;
                continue;
            }

            if (c == '*' || c == '_')
            {
                if (Emphasis(text, i) is { } emphasis)
                {
                    Add(emphasis.Kind, emphasis.Inner);
                    i = emphasis.End;
                }
                else
                {
                    // Markers that open nothing (or not yet, mid-stream) are text, the whole run of them.
                    while (i < text.Length && text[i] == c)
                    {
                        plain.Append(text[i++]);
                    }

                    i--;
                }

                continue;
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Add(InlineKind.Strike, text[(i + 2)..end]);
                    i = end + 1;
                    continue;
                }
            }

            plain.Append(c);
        }

        Add(InlineKind.Text, string.Empty);
        return spans;
    }

    private static (string Text, string Url, int End)? LinkAt(string text, int start)
    {
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return null;
        }

        var end = text.IndexOf(')', close + 2);
        return end < 0 ? null : (text[(start + 1)..close], text[(close + 2)..end].Trim(), end);
    }

    private static (InlineKind Kind, string Inner, int End)? Emphasis(string text, int start)
    {
        var marker = text[start];
        var count = 0;
        while (start + count < text.Length && text[start + count] == marker && count < 3)
        {
            count++;
        }

        // An opening marker is followed by text, and "_" inside words (snake_case) is not emphasis.
        if (start + count >= text.Length || char.IsWhiteSpace(text[start + count])
            || (marker == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1])))
        {
            return null;
        }

        var fence = new string(marker, count);
        var end = text.IndexOf(fence, start + count, StringComparison.Ordinal);

        // A closing marker follows text (not a space or a backslash escape).
        while (end > 0 && (char.IsWhiteSpace(text[end - 1]) || text[end - 1] == '\\'))
        {
            end = text.IndexOf(fence, end + count, StringComparison.Ordinal);
        }

        if (end <= start + count)
        {
            return null;
        }

        var kind = count switch { 1 => InlineKind.Italic, 2 => InlineKind.Bold, _ => InlineKind.BoldItalic };
        return (kind, text[(start + count)..end], end + count - 1);
    }
}
