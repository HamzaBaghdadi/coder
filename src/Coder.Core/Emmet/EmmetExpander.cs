using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Coder.Core.Emmet;

/// <summary>
/// Turns a parsed abbreviation into HTML in snippet syntax: repeats are unrolled and <c>$</c> numbered,
/// snippet names (<c>inp</c>, <c>!</c>) replaced by their definitions, implicit tags named from their
/// parent (<c>li</c> in a <c>ul</c>), then the tree is written the way Emmet formats it (block elements
/// on their own lines, inline ones kept together). Empty attributes and empty elements become tab
/// stops, numbered in document order.
/// </summary>
internal sealed partial class EmmetExpander
{
    /// <summary>Snippet fields are renumbered per use, so two uses of one snippet do not mirror each other.</summary>
    private const int ScopeSize = 1000;

    /// <summary>Three inline siblings in a row go on their own lines (Emmet's <c>output.inlineBreak</c>).</summary>
    private const int InlineBreak = 3;

    private readonly EmmetOptions _options;
    private readonly StringBuilder _output = new();
    private readonly Dictionary<int, int> _fields = [];
    private int _nextField = 1;
    private int _scopes;

    private EmmetExpander(EmmetOptions options) => _options = options;

    public static string? Expand(string abbreviation, EmmetOptions options)
    {
        if (EmmetParser.Parse(abbreviation, EmmetSnippets.Html.ContainsKey) is not { } root)
        {
            return null;
        }

        var expander = new EmmetExpander(options);
        var nodes = Unroll(root, null);
        nodes = expander.Resolve(nodes, []);
        NameImplicit(nodes, parent: null);
        if (nodes.Count == 0)
        {
            return null;
        }

        expander.WriteList(nodes, parent: null, level: 0);
        return expander._output.ToString();
    }

    // Repeats and numbering -----------------------------------------------------------------------

    /// <summary>Copies each node <c>*N</c> times, replacing <c>$</c> with the copy's number.</summary>
    private static List<EmmetNode> Unroll(EmmetNode node, (int Index, int Count)? repeat)
    {
        var result = new List<EmmetNode>();
        for (var i = 1; i <= node.Repeat; i++)
        {
            var current = node.Repeat > 1 ? (i, node.Repeat) : repeat;
            if (node.IsGroup)
            {
                result.AddRange(node.Children.SelectMany(child => Unroll(child, current)));
                continue;
            }

            var (index, count) = current ?? (1, 1);
            var copy = new EmmetNode
            {
                Name = node.Name is null ? null : Number(node.Name, index, count),
                Text = node.Text is null ? null : Number(node.Text, index, count),
                SelfClosing = node.SelfClosing,
            };
            copy.Attributes.AddRange(node.Attributes.Select(a => a with
            {
                Name = Number(a.Name, index, count),
                Value = a.Value is null ? null : Number(a.Value, index, count),
            }));
            copy.Children.AddRange(node.Children.SelectMany(child => Unroll(child, current)));
            result.Add(copy);
        }

        return result;
    }

    /// <summary><c>$</c> → 1, <c>$$$</c> → 001, <c>$@-</c> counts down, <c>$@3</c> starts at 3; <c>${</c> is a field, <c>\$</c> a dollar.</summary>
    internal static string Number(string text, int index, int count)
    {
        if (!text.Contains('$', StringComparison.Ordinal))
        {
            return text;
        }

        return NumberingPattern().Replace(text, match =>
        {
            if (match.Value.StartsWith('\\'))
            {
                return match.Value;
            }

            var width = match.Groups["dollars"].Length;
            var reverse = match.Groups["reverse"].Success;
            var start = match.Groups["start"].Success ? int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture) : 1;
            var value = reverse ? start + count - index : start + index - 1;
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        });
    }

    [GeneratedRegex(@"\\\$|(?<dollars>\$+)(?!\{)(?:@(?<reverse>-)?(?<start>\d+)?)?")]
    private static partial Regex NumberingPattern();

    // Snippets ------------------------------------------------------------------------------------

    /// <summary>Replaces snippet names with their definitions, merging what was typed into the result.</summary>
    private List<EmmetNode> Resolve(List<EmmetNode> nodes, HashSet<string> active)
    {
        var result = new List<EmmetNode>();
        foreach (var node in nodes)
        {
            var children = Resolve(node.Children, active);
            node.Children.Clear();
            node.Children.AddRange(children);

            if (node.Name is not { } name || active.Contains(name) || !EmmetSnippets.Html.TryGetValue(name, out var definition)
                || EmmetParser.Parse(definition, EmmetSnippets.Html.ContainsKey) is not { } snippetRoot)
            {
                result.Add(node);
                continue;
            }

            var scope = ++_scopes * ScopeSize;
            var expanded = Unroll(snippetRoot, null);
            foreach (var snippetNode in Flatten(expanded))
            {
                snippetNode.Text = snippetNode.Text is null ? null : Rescope(snippetNode.Text, scope);
                for (var i = 0; i < snippetNode.Attributes.Count; i++)
                {
                    var attribute = snippetNode.Attributes[i];
                    snippetNode.Attributes[i] = attribute with { Value = attribute.Value is null ? null : Rescope(attribute.Value, scope) };
                }
            }

            active.Add(name);
            expanded = Resolve(expanded, active);
            active.Remove(name);
            Merge(node, expanded);
            result.AddRange(expanded);
        }

        return result;
    }

    /// <summary>
    /// What was typed with a snippet name lands on the snippet's (last) element: attributes override
    /// or join its own, text replaces its text, children go into its deepest last element.
    /// </summary>
    private static void Merge(EmmetNode typed, List<EmmetNode> snippet)
    {
        if (snippet.LastOrDefault(n => n.Name is not null) is not { } target)
        {
            snippet.AddRange(typed.Children);
            return;
        }

        foreach (var attribute in typed.Attributes)
        {
            target.SetAttribute(attribute);
        }

        if (typed.Text is not null)
        {
            target.Text = typed.Text;
        }

        target.SelfClosing |= typed.SelfClosing;
        var deepest = target;
        while (deepest.Children.LastOrDefault(n => n.Name is not null) is { } last)
        {
            deepest = last;
        }

        deepest.Children.AddRange(typed.Children);
    }

    private static IEnumerable<EmmetNode> Flatten(IEnumerable<EmmetNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    /// <summary>Moves a snippet's <c>${1}</c> fields to its own range: <c>${2001}</c>.</summary>
    private static string Rescope(string text, int scope) =>
        FieldNumberPattern().Replace(text, m => m.Value.StartsWith('\\')
            ? m.Value
            : m.Groups["brace"].Value + (scope + int.Parse(m.Groups["number"].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));

    [GeneratedRegex(@"\\\$|(?<brace>\$\{?)(?<number>\d+)")]
    private static partial Regex FieldNumberPattern();

    // Implicit tags -------------------------------------------------------------------------------

    private static void NameImplicit(List<EmmetNode> nodes, string? parent)
    {
        foreach (var node in nodes)
        {
            if (node.Name is null && !node.IsText)
            {
                node.Name = ImplicitTag(parent);
            }

            NameImplicit(node.Children, node.Name ?? parent);
        }
    }

    private static string ImplicitTag(string? parent) => parent?.ToLowerInvariant() switch
    {
        "ul" or "ol" => "li",
        "table" or "tbody" or "thead" or "tfoot" => "tr",
        "tr" => "td",
        "select" or "optgroup" or "datalist" => "option",
        "map" => "area",
        "audio" or "video" => "source",
        "object" => "param",
        "colgroup" => "col",
        { } name when EmmetSnippets.Inline.Contains(name) => "span",
        _ => "div",
    };

    // Writing -------------------------------------------------------------------------------------

    private void WriteList(List<EmmetNode> items, EmmetNode? parent, int level)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ShouldFormat(items, i, parent))
            {
                NewLine(level);
            }

            WriteNode(items[i], level);
        }
    }

    private void WriteNode(EmmetNode node, int level)
    {
        if (node.Name is not { } name)
        {
            WriteValue(node.Text ?? string.Empty);
            return;
        }

        _output.Append('<').Append(name);
        foreach (var attribute in node.Attributes)
        {
            _output.Append(' ').Append(attribute.Name);
            if (attribute.Boolean)
            {
                continue;
            }

            _output.Append("=\"");
            if (attribute.Value is null)
            {
                NewField();
            }
            else
            {
                WriteValue(attribute.Value);
            }

            _output.Append('"');
        }

        _output.Append('>');
        if (node.SelfClosing || (EmmetSnippets.Void.Contains(name.ToLowerInvariant()) && node.Children.Count == 0 && node.Text is null))
        {
            return;
        }

        // Emmet leaves <html>'s children unindented and always opens up <body>.
        var childLevel = name.Equals("html", StringComparison.OrdinalIgnoreCase) ? level : level + 1;
        var multiline = false;
        if (node.Text is not null)
        {
            WriteValue(node.Text);
        }

        if (node.Children.Count > 0)
        {
            multiline = Enumerable.Range(0, node.Children.Count).Any(i => ShouldFormat(node.Children, i, node));
            WriteList(node.Children, node, childLevel);
        }
        else if (node.Text is null)
        {
            if (name.Equals("body", StringComparison.OrdinalIgnoreCase))
            {
                NewLine(childLevel);
                multiline = true;
            }

            NewField();
        }

        if (multiline)
        {
            NewLine(level);
        }

        _output.Append("</").Append(name).Append('>');
    }

    /// <summary>Emmet's rule for putting a node on its own line.</summary>
    private static bool ShouldFormat(List<EmmetNode> items, int index, EmmetNode? parent)
    {
        var node = items[index];
        if (index == 0 && parent is null)
        {
            return false; // the very first node stays where the abbreviation was
        }

        if (node.IsText)
        {
            return (index > 0 && items[index - 1].IsText) || (index + 1 < items.Count && items[index + 1].IsText)
                   || node.Text!.Contains('\n', StringComparison.Ordinal);
        }

        if (!IsInline(node))
        {
            return true;
        }

        if (index == 0 ? items.Any(item => !IsInline(item)) : !IsInline(items[index - 1]))
        {
            return true; // first of a mixed list, or right after a block
        }

        var adjacent = 1;
        for (var i = index - 1; i >= 0 && IsInlineElement(items[i]); i--)
        {
            adjacent++;
        }

        for (var i = index + 1; i < items.Count && IsInlineElement(items[i]); i++)
        {
            adjacent++;
        }

        return adjacent >= InlineBreak || Enumerable.Range(0, node.Children.Count).Any(i => ShouldFormat(node.Children, i, node));
    }

    private static bool IsInline(EmmetNode node) => node.IsText || IsInlineElement(node);

    private static bool IsInlineElement(EmmetNode node) => node.Name is { } name && EmmetSnippets.Inline.Contains(name.ToLowerInvariant());

    private void NewLine(int level)
    {
        _output.Append('\n');
        for (var i = 0; i < level; i++)
        {
            _output.Append(_options.Indent);
        }
    }

    private void NewField() => _output.Append("${").Append(_nextField++.ToString(CultureInfo.InvariantCulture)).Append('}');

    /// <summary>
    /// Copies text or an attribute value: <c>${n:placeholder}</c> and <c>$n</c> fields are renumbered in
    /// document order (one number per source field, so mirrors stay mirrors), escapes are kept, and
    /// any other <c>$</c> or <c>}</c> is escaped so it stays literal.
    /// </summary>
    private void WriteValue(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                _output.Append(c).Append(value[++i]);
            }
            else if (c == '$' && i + 2 < value.Length && value[i + 1] == '{' && char.IsAsciiDigit(value[i + 2]))
            {
                var end = i + 2;
                while (end < value.Length && char.IsAsciiDigit(value[end]))
                {
                    end++;
                }

                var number = Field(value[(i + 2)..end]);
                _output.Append("${").Append(number);
                if (end < value.Length && value[end] == ':')
                {
                    var close = ClosingBrace(value, end + 1);
                    _output.Append(':');
                    WriteValue(value[(end + 1)..close]);
                    end = close;
                }

                _output.Append('}');
                i = end; // at the closing brace
            }
            else if (c == '$' && i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
            {
                var end = i + 1;
                while (end < value.Length && char.IsAsciiDigit(value[end]))
                {
                    end++;
                }

                _output.Append("${").Append(Field(value[(i + 1)..end])).Append('}');
                i = end - 1;
            }
            else
            {
                if (c is '$' or '}')
                {
                    _output.Append('\\');
                }

                _output.Append(c);
            }
        }
    }

    private string Field(string source)
    {
        var number = int.Parse(source, CultureInfo.InvariantCulture);
        if (!_fields.TryGetValue(number, out var mapped))
        {
            _fields[number] = mapped = _nextField++;
        }

        return mapped.ToString(CultureInfo.InvariantCulture);
    }

    private static int ClosingBrace(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && depth-- == 0)
            {
                return i;
            }
        }

        return text.Length;
    }
}
