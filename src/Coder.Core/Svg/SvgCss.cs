using System.Text;

namespace Coder.Core.Svg;

/// <summary>One CSS declaration, e.g. <c>fill: red !important</c>.</summary>
internal readonly record struct SvgDeclaration(string Name, string Value, bool Important);

/// <summary>
/// A rule of an embedded <c>&lt;style&gt;</c> sheet with one compound selector: an optional type (or <c>*</c>),
/// an optional <c>#id</c> and any number of <c>.class</c>es. Rules with combinators, pseudo-classes or attribute
/// selectors are dropped, as are at-rules.
/// </summary>
internal sealed record SvgCssRule(string? Type, string? Id, IReadOnlyList<string> Classes, int Specificity, int Order,
    IReadOnlyList<SvgDeclaration> Declarations)
{
    public bool Matches(string type, string? id, IReadOnlyCollection<string> classes) =>
        (Type is null || string.Equals(Type, type, StringComparison.Ordinal))
        && (Id is null || string.Equals(Id, id, StringComparison.Ordinal))
        && Classes.All(classes.Contains);
}

internal static class SvgCss
{
    /// <summary>Parses <c>a: b; c: d</c>; malformed declarations are skipped.</summary>
    public static List<SvgDeclaration> ParseDeclarations(string? text)
    {
        var list = new List<SvgDeclaration>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        foreach (var part in SplitOutside(StripComments(text), ';'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            var important = false;
            var bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].Trim();
            }

            if (name.Length > 0 && value.Length > 0)
            {
                list.Add(new(name, value, important));
            }
        }

        return list;
    }

    /// <summary>Parses a style sheet; <paramref name="order"/> continues across several sheets.</summary>
    public static void ParseSheet(string css, List<SvgCssRule> rules, ref int order)
    {
        var text = StripComments(css);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                break;
            }

            var prelude = text[i..open].Trim();

            // At-rules: "@import ...;" statements before the block, or "@media {...}" blocks skipped whole.
            while (prelude.StartsWith('@') && prelude.Contains(';', StringComparison.Ordinal))
            {
                prelude = prelude[(prelude.IndexOf(';', StringComparison.Ordinal) + 1)..].Trim();
            }

            var close = MatchingBrace(text, open);
            if (close < 0)
            {
                break;
            }

            if (!prelude.StartsWith('@'))
            {
                var declarations = ParseDeclarations(text[(open + 1)..close]);
                foreach (var selectorText in prelude.Split(','))
                {
                    if (TryParseSelector(selectorText.Trim(), out var type, out var id, out var classes))
                    {
                        var specificity = (id is null ? 0 : 100) + (classes.Count * 10) + (type is null ? 0 : 1);
                        rules.Add(new(type, id, classes, specificity, order++, declarations));
                    }
                }
            }

            i = close + 1;
        }
    }

    private static bool TryParseSelector(string selector, out string? type, out string? id, out List<string> classes)
    {
        type = null;
        id = null;
        classes = [];
        if (selector.Length == 0 || selector.IndexOfAny([' ', '>', '+', '~', ':', '[', '\t', '\n']) >= 0)
        {
            return false;
        }

        var i = 0;
        var head = ReadIdentifier(selector, ref i);
        if (head.Length > 0)
        {
            type = head;
        }
        else if (i < selector.Length && selector[i] == '*')
        {
            i++;
        }

        while (i < selector.Length)
        {
            var marker = selector[i++];
            var name = ReadIdentifier(selector, ref i);
            if (name.Length == 0)
            {
                return false;
            }

            if (marker == '.')
            {
                classes.Add(name);
            }
            else if (marker == '#' && id is null)
            {
                id = name;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadIdentifier(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_'))
        {
            i++;
        }

        return text[start..i];
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripComments(string text)
    {
        if (!text.Contains("/*", StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf("/*", i, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, i, text.Length - i);
                break;
            }

            builder.Append(text, i, start - i);
            var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            i = end < 0 ? text.Length : end + 2;
        }

        return builder.ToString();
    }

    /// <summary>Splits on <paramref name="separator"/> outside parentheses and quotes (so url(a;b) stays whole).</summary>
    private static List<string> SplitOutside(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }
}
