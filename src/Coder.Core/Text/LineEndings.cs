namespace Coder.Core.Text;

public enum LineEnding
{
    LF,
    CRLF,
}

/// <summary>
/// Documents are edited with <c>'\n'</c> line breaks only. The file's original style is detected on
/// load and restored on save, so every other component can ignore <c>'\r'</c>.
/// </summary>
public static class LineEndings
{
    public static LineEnding Platform => OperatingSystem.IsWindows() ? LineEnding.CRLF : LineEnding.LF;

    /// <summary>Picks the dominant style. Files without any line break use the platform default.</summary>
    public static LineEnding Detect(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0)
        {
            return Platform;
        }

        return crlf > lf ? LineEnding.CRLF : LineEnding.LF;
    }

    /// <summary>Converts CRLF and lone CR to LF.</summary>
    public static string Normalize(string text) =>
        text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;

    public static string Apply(string normalizedText, LineEnding ending) =>
        ending == LineEnding.CRLF ? normalizedText.Replace("\n", "\r\n", StringComparison.Ordinal) : normalizedText;

    public static string DisplayName(this LineEnding ending) => ending == LineEnding.CRLF ? "CRLF" : "LF";
}
