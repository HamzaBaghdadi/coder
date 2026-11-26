namespace Coder.Core.Text;

/// <summary>
/// Converts between character columns and visual columns (where a tab advances to the next tab stop).
/// Rendering, mouse hit-testing and vertical caret movement all work in visual columns.
/// </summary>
public static class TextColumns
{
    public static int ToVisual(ReadOnlySpan<char> line, int column, int tabSize)
    {
        var visual = 0;
        var end = Math.Min(column, line.Length);
        for (var i = 0; i < end; i++)
        {
            visual = line[i] == '\t' ? NextTabStop(visual, tabSize) : visual + 1;
        }

        return visual + Math.Max(0, column - line.Length);
    }

    /// <summary>The character column closest to <paramref name="visualColumn"/>.</summary>
    public static int FromVisual(ReadOnlySpan<char> line, int visualColumn, int tabSize)
    {
        var visual = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var next = line[i] == '\t' ? NextTabStop(visual, tabSize) : visual + 1;
            if (visualColumn < next)
            {
                // Snap to whichever side of the character is nearer.
                return visualColumn - visual <= next - visualColumn ? i : i + 1;
            }

            visual = next;
        }

        return line.Length;
    }

    /// <summary>Expands tabs to spaces for display.</summary>
    public static string ExpandTabs(string line, int tabSize)
    {
        if (!line.Contains('\t', StringComparison.Ordinal))
        {
            return line;
        }

        var builder = new System.Text.StringBuilder(line.Length + 16);
        foreach (var c in line)
        {
            if (c == '\t')
            {
                builder.Append(' ', tabSize - (builder.Length % tabSize));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static int LeadingWhitespaceLength(ReadOnlySpan<char> line)
    {
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    private static int NextTabStop(int visual, int tabSize) => (visual / tabSize + 1) * tabSize;
}
