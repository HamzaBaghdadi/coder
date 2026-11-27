using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// Indentation helpers: guessing a file's indentation from its text (VS Code's "Detect Indentation"), and the
/// clean-ups applied when saving (trim trailing whitespace, a final newline).
/// </summary>
public static class Indentation
{
    /// <summary>How many lines are looked at; enough to decide, cheap on big files.</summary>
    private const int SampleLines = 2000;

    /// <summary>
    /// The file's indentation, or null when it has too little indented text to tell. Tabs win when more lines
    /// start with a tab than with spaces; otherwise the most common step between successive indented lines
    /// (2, 4 or 8, else whatever is most common) is the tab size.
    /// </summary>
    public static (bool InsertSpaces, int TabSize)? Detect(TextDocument document)
    {
        var tabLines = 0;
        var spaceLines = 0;
        var steps = new Dictionary<int, int>();
        var previousSpaces = 0;
        var lines = Math.Min(document.LineCount, SampleLines);
        for (var line = 0; line < lines; line++)
        {
            var text = document.GetLineText(line);
            if (text.Length == 0 || text.All(char.IsWhiteSpace))
            {
                continue; // blank lines say nothing
            }

            if (text[0] == '\t')
            {
                tabLines++;
                previousSpaces = 0;
                continue;
            }

            var spaces = 0;
            while (spaces < text.Length && text[spaces] == ' ')
            {
                spaces++;
            }

            if (spaces > 0 && (spaces < text.Length && text[spaces] == '*'))
            {
                continue; // the aligned " * " of a block comment isn't indentation
            }

            if (spaces > 0)
            {
                spaceLines++;
            }

            var step = Math.Abs(spaces - previousSpaces);
            if (step is > 1 and <= 8)
            {
                steps[step] = steps.GetValueOrDefault(step) + 1;
            }

            previousSpaces = spaces;
        }

        if (tabLines == 0 && spaceLines == 0)
        {
            return null;
        }

        if (tabLines > spaceLines)
        {
            return (false, 4); // tabs: the tab size is the user's setting, 4 is only a fallback
        }

        if (steps.Count == 0)
        {
            return null;
        }

        var size = steps.OrderByDescending(s => s.Value).ThenBy(s => s.Key).First().Key;
        return (true, size);
    }

    /// <summary>Edits that remove spaces and tabs at the ends of lines (offsets in the current text).</summary>
    public static IReadOnlyList<(int Start, int End, string Text)> TrimTrailingWhitespace(TextDocument document)
    {
        var edits = new List<(int, int, string)>();
        for (var line = 0; line < document.LineCount; line++)
        {
            var text = document.GetLineText(line);
            var end = text.Length;
            while (end > 0 && text[end - 1] is ' ' or '\t')
            {
                end--;
            }

            if (end < text.Length)
            {
                var start = document.GetLineStart(line);
                edits.Add((start + end, start + text.Length, string.Empty));
            }
        }

        return edits;
    }

    /// <summary>An edit adding a newline at the end when the text is not empty and doesn't end with one.</summary>
    public static IReadOnlyList<(int Start, int End, string Text)> InsertFinalNewline(TextDocument document)
    {
        var last = document.LineCount - 1;
        return document.Length > 0 && document.GetLineText(last).Length > 0
            ? [(document.Length, document.Length, "\n")]
            : [];
    }
}
