using System.Text.RegularExpressions;
using Coder.Presentation.Agent;

namespace Coder.Presentation.Editors;

/// <summary>
/// Reads a Markdown file (a README, docs) into blocks for the preview. Files written for GitHub mix in HTML,
/// so the common parts are turned into Markdown first: <c>&lt;img&gt;</c> becomes an image, <c>&lt;br&gt;</c>
/// a line break, links around badges keep the badge, comments and other tags disappear (their text stays).
/// </summary>
public static partial class MarkdownDocument
{
    public static IReadOnlyList<ChatBlock> Parse(string markdown) => ChatMarkdown.Parse(Prepare(markdown));

    /// <summary>The HTML-to-Markdown pass (internal for tests).</summary>
    public static string Prepare(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = Comment().Replace(text, string.Empty);
        text = Image().Replace(text, match =>
        {
            var src = Attribute(match.Value, "src");
            return src is null ? string.Empty : $"![{Attribute(match.Value, "alt") ?? string.Empty}]({src})";
        });
        text = Break().Replace(text, "\n");

        // [![badge](image)](link) → the badge alone: images can't sit inside a link run in the preview.
        text = LinkedImage().Replace(text, "$1");

        // Remaining tags (p, div, a, details, summary, picture, sup…) go; their text stays.
        text = Tag().Replace(text, string.Empty);
        return System.Net.WebUtility.HtmlDecode(text);
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase) is { Success: true } match
            ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value
            : null;

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Image();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"\[(!\[[^\]]*\]\([^)]*\))\]\([^)]*\)")]
    private static partial Regex LinkedImage();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9-]*(\s[^<>]*)?/?>")]
    private static partial Regex Tag();
}
