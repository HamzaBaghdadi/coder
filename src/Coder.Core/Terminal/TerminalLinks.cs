using System.Text.RegularExpressions;

namespace Coder.Core.Terminal;

/// <summary>What a link in the terminal points at.</summary>
public enum TerminalLinkKind
{
    Url,
    File,
}

/// <summary>
/// A link found in a terminal line: where it is (cell columns) and what it opens. For files, the path as
/// written (maybe relative) and the 1-based line and column after it ("src/app.cs:12:5", "app.cs(12,5)").
/// </summary>
public sealed record TerminalLink(int StartColumn, int EndColumn, TerminalLinkKind Kind, string Target, int? Line = null, int? Column = null);

/// <summary>
/// Finds what is worth clicking in terminal output, like VS Code: web addresses, and file paths that
/// compilers, test runners, linters and stack traces print, with the line (and column) they point at.
/// Whether a relative path really exists is for the caller to check.
/// </summary>
public static partial class TerminalLinks
{
    /// <summary>The links in one line of the buffer, in column order.</summary>
    public static IReadOnlyList<TerminalLink> Find(TerminalLine line)
    {
        var (text, columnOf) = TerminalText.Of(line);
        return Find(text).Select(link => link with { StartColumn = columnOf[link.StartColumn], EndColumn = columnOf[link.EndColumn] }).ToList();
    }

    /// <summary>The links in a text; here the columns are character indexes.</summary>
    public static IReadOnlyList<TerminalLink> Find(string text)
    {
        var links = new List<TerminalLink>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
            if (url.EndsWith(')') && !url.Contains('(', StringComparison.Ordinal))
            {
                url = url[..^1]; // "(see https://example.com)"
            }

            links.Add(new TerminalLink(match.Index, match.Index + url.Length, TerminalLinkKind.Url, url));
        }

        foreach (Match match in PathPattern().Matches(text))
        {
            if (links.Any(l => match.Index < l.EndColumn && match.Index + match.Length > l.StartColumn))
            {
                continue; // part of a URL
            }

            var path = match.Groups["path"].Value;
            int? Number(string group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            var lineNumber = Number("line") ?? Number("pline");
            var column = Number("column") ?? Number("pcolumn");
            links.Add(new TerminalLink(match.Index, match.Index + match.Length, TerminalLinkKind.File, path, lineNumber, column));
        }

        return links.OrderBy(l => l.StartColumn).ToList();
    }

    [GeneratedRegex("""https?://[^\s<>"'`\]\[{}|\\^]+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>
    /// A path with a file extension: absolute ("C:\src\app.cs", "/home/me/app.py") or relative
    /// ("src/app.cs", "./app.ts", "..\lib\x.go"), then optionally ":12", ":12:5" or "(12,5)" (MSBuild).
    /// </summary>
    [GeneratedRegex("""(?<path>(?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|[\\/])?(?:[\w.@-]+[\\/])*[\w@-][\w.@-]*\.[A-Za-z][A-Za-z0-9]{0,9})(?:(?::(?<line>\d+)(?::(?<column>\d+))?)|(?:\((?<pline>\d+)(?:,(?<pcolumn>\d+))?\)))?""")]
    private static partial Regex PathPattern();
}

/// <summary>The text of a terminal line, with the cell column of each character (wide characters take two cells).</summary>
public static class TerminalText
{
    /// <summary>
    /// The line's text, and for each character index the cell it is drawn in; one more entry at the end
    /// gives the column just past the text.
    /// </summary>
    public static (string Text, IReadOnlyList<int> ColumnOf) Of(TerminalLine line)
    {
        var text = new System.Text.StringBuilder();
        var columnOf = new List<int>();
        var cells = line.Cells;
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];
            if (cell.IsContinuation)
            {
                continue;
            }

            var piece = cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);
            text.Append(piece);
            for (var i = 0; i < piece.Length; i++)
            {
                columnOf.Add(column);
            }
        }

        columnOf.Add(cells.Length);
        return (text.ToString(), columnOf);
    }
}
