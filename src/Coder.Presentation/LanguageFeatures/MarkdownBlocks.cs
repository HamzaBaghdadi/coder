using System.Text;
using System.Text.RegularExpressions;

namespace Coder.Presentation.LanguageFeatures;

public enum MarkdownBlockKind
{
    Paragraph,
    Code,
    Rule,
}

/// <summary>One block of hover text: a paragraph, a fenced code block (with its language) or a divider.</summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, string? Language = null);

/// <summary>
/// Reads the Markdown that language servers put in hovers into blocks the UI can show: code fences
/// stay code (to be syntax colored), prose loses its markup (links keep their text, emphasis and
/// inline-code markers go). Not a full Markdown renderer, which hovers do not need.
/// </summary>
public static partial class MarkdownBlocks
{
    public static IReadOnlyList<MarkdownBlock> Parse(string text, bool isMarkdown)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (!isMarkdown)
        {
            return normalized.Length == 0 ? [] : [new MarkdownBlock(MarkdownBlockKind.Paragraph, normalized)];
        }

        var blocks = new List<MarkdownBlock>();
        var paragraph = new StringBuilder();
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(blocks, paragraph);
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                if (code.Count > 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join('\n', code), language.Length > 0 ? language : null));
                }
            }
            else if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, string.Empty));
            }
            else if (trimmed.Length == 0)
            {
                FlushParagraph(blocks, paragraph);
            }
            else
            {
                // Lists and headings keep their own line; other lines flow into one paragraph.
                var breaksLine = trimmed[0] is '#' or '-' or '*' or '+' || char.IsDigit(trimmed[0]);
                if (paragraph.Length > 0)
                {
                    paragraph.Append(breaksLine || line.EndsWith("  ", StringComparison.Ordinal) ? '\n' : ' ');
                }

                paragraph.Append(CleanInline(trimmed.TrimStart('#').TrimStart()));
            }
        }

        FlushParagraph(blocks, paragraph);
        while (blocks.Count > 0 && blocks[^1].Kind == MarkdownBlockKind.Rule)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    private static void FlushParagraph(List<MarkdownBlock> blocks, StringBuilder paragraph)
    {
        if (paragraph.Length > 0)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }
    }

    /// <summary>Removes inline markup: <c>[text](url)</c> → text, <c>**bold**</c> and <c>`code`</c> markers, backslash escapes.</summary>
    private static string CleanInline(string line)
    {
        line = LinkPattern().Replace(line, "$1");
        line = line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        return EscapePattern().Replace(line, "$1");
    }

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!<>|])")]
    private static partial Regex EscapePattern();
}
