namespace Coder.Core.Terminal;

/// <summary>A match in the terminal: its line (history and screen counted together) and its cell columns.</summary>
public readonly record struct TerminalMatch(int Line, int StartColumn, int EndColumn);

/// <summary>
/// Find in the terminal: every match of a text in the history and on screen, as cell columns, so
/// wide characters (CJK, emoji, two cells each) are highlighted where they are drawn.
/// </summary>
public static class TerminalSearch
{
    public static IReadOnlyList<TerminalMatch> FindAll(TerminalBuffer buffer, string query, bool matchCase)
    {
        if (query.Length == 0)
        {
            return [];
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TerminalMatch>();
        for (var line = 0; line < buffer.TotalLines; line++)
        {
            var (text, columnOf) = TerminalText.Of(buffer.LineAt(line));
            for (var at = text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison))
            {
                matches.Add(new TerminalMatch(line, columnOf[at], columnOf[at + query.Length]));
            }
        }

        return matches;
    }
}
