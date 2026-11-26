using System.Globalization;
using System.Text;

namespace Coder.Core.Terminal;

public enum TerminalColorKind : byte
{
    Default,

    /// <summary>One of the 256 palette colors (0-15 are the themeable ANSI colors).</summary>
    Indexed,

    /// <summary>A 24-bit color; <see cref="TerminalColor.Value"/> is 0xRRGGBB.</summary>
    Rgb,
}

public readonly record struct TerminalColor(TerminalColorKind Kind, int Value)
{
    public static TerminalColor Default => default;

    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, index);

    public static TerminalColor Rgb(int r, int g, int b) => new(TerminalColorKind.Rgb, (r << 16) | (g << 8) | b);
}

[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strikethrough = 32,
    Hidden = 64,
}

/// <summary>How a cell is drawn (SGR state at the time it was written).</summary>
public readonly record struct CellStyle(TerminalColor Foreground, TerminalColor Background, CellAttributes Attributes)
{
    public static CellStyle Default => default;
}

/// <summary>
/// One character cell. <see cref="Rune"/> 0 means empty. A wide character (CJK, most emoji) fills two
/// cells: the first holds it with <see cref="Width"/> 2, the second is a continuation with width 0.
/// </summary>
public readonly record struct TerminalCell(int Rune, CellStyle Style, byte Width = 1)
{
    public static TerminalCell Blank(CellStyle style) => new(0, style with { Attributes = CellAttributes.None, Foreground = TerminalColor.Default });

    public bool IsContinuation => Width == 0;
}

/// <summary>Display width of a character in terminal cells (a compact version of wcwidth).</summary>
public static class CellWidth
{
    public static int Of(int codepoint)
    {
        if (codepoint < 0x300)
        {
            return 1;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(codepoint);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(codepoint) ? 2 : 1;
    }

    private static bool IsWide(int c) =>
        (c >= 0x1100 && c <= 0x115F) ||   // Hangul Jamo
        (c >= 0x2E80 && c <= 0x303E) ||   // CJK radicals, punctuation
        (c >= 0x3041 && c <= 0x33FF) ||   // Hiragana, Katakana, CJK symbols
        (c >= 0x3400 && c <= 0x4DBF) ||   // CJK extension A
        (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK unified ideographs
        (c >= 0xA000 && c <= 0xA4CF) ||   // Yi
        (c >= 0xAC00 && c <= 0xD7A3) ||   // Hangul syllables
        (c >= 0xF900 && c <= 0xFAFF) ||   // CJK compatibility ideographs
        (c >= 0xFE30 && c <= 0xFE4F) ||   // CJK compatibility forms
        (c >= 0xFF00 && c <= 0xFF60) ||   // Fullwidth forms
        (c >= 0xFFE0 && c <= 0xFFE6) ||
        (c >= 0x1F300 && c <= 0x1F64F) || // emoji, pictographs
        (c >= 0x1F900 && c <= 0x1F9FF) ||
        (c >= 0x20000 && c <= 0x3FFFD);   // CJK extensions B+

    /// <summary>The text of a run of cells (continuations skipped, empty cells as spaces).</summary>
    public static string TextOf(IEnumerable<TerminalCell> cells)
    {
        var builder = new StringBuilder();
        foreach (var cell in cells)
        {
            if (cell.IsContinuation)
            {
                continue;
            }

            builder.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString();
    }
}
