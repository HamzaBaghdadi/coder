using System.Globalization;
using System.Text;
using Avalonia.Media;
using Coder.App.Theme;

namespace Coder.App.Editor;

/// <summary>
/// Chooses, for every character of a line, a font that can draw it: the coding font when it has the
/// glyph, otherwise the first installed font from <see cref="Typography.CodeFallbackFamilies"/>
/// (Arabic, CJK, emoji…).
/// <para>
/// The editor does this itself instead of relying on the text engine's automatic fallback, which in
/// Avalonia 12 can start resolving Arabic to the coding font (drawing empty boxes) once semi-bold UI
/// text has been rendered. Explicit fonts make the result deterministic.
/// </para>
/// </summary>
internal static class GlyphFallback
{
    private static readonly Lazy<IReadOnlyList<(Typeface Typeface, GlyphTypeface Glyphs)>> Fonts = new(LoadFonts);
    private static readonly Dictionary<int, int> FontIndexByCodepoint = [];

    /// <summary>The coding font that is actually installed (first entry of the chain).</summary>
    public static Typeface Primary => Fonts.Value[0].Typeface;

    /// <summary>
    /// Runs of text that need a font other than the coding font, as (start, length, typeface).
    /// Characters the coding font can draw are not listed.
    /// </summary>
    public static List<(int Start, int Length, Typeface Typeface)> FallbackRuns(string text)
    {
        var runs = new List<(int Start, int Length, Typeface Typeface)>();
        var previousFont = 0;
        for (var i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var length);

            // Joiners, variation selectors and combining marks stay with the character before them, so
            // emoji sequences and Arabic diacritics are never split across fonts.
            var font = StaysWithPrevious(rune) ? previousFont : FontIndexFor(rune.Value);
            if (font != 0)
            {
                var continuesRun = font == previousFont && runs.Count > 0 && runs[^1].Start + runs[^1].Length == i;
                if (continuesRun)
                {
                    runs[^1] = (runs[^1].Start, runs[^1].Length + length, runs[^1].Typeface);
                }
                else
                {
                    runs.Add((i, length, Fonts.Value[font].Typeface));
                }
            }

            previousFont = font;
            i += length;
        }

        return runs;
    }

    private static int FontIndexFor(int codepoint)
    {
        if (codepoint < 0x80)
        {
            return 0; // ASCII: always the coding font
        }

        if (!FontIndexByCodepoint.TryGetValue(codepoint, out var index))
        {
            index = 0;
            var fonts = Fonts.Value;
            for (var i = 0; i < fonts.Count; i++)
            {
                if (fonts[i].Glyphs.CharacterToGlyphMap.ContainsGlyph(codepoint))
                {
                    index = i;
                    break;
                }
            }

            FontIndexByCodepoint[codepoint] = index;
        }

        return index;
    }

    private static bool StaysWithPrevious(Rune rune) =>
        rune.Value is 0x200D or 0x200C || (rune.Value is >= 0xFE00 and <= 0xFE0F) || (rune.Value is >= 0x1F3FB and <= 0x1F3FF)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

    /// <summary>The installed coding font, then every installed fallback font, in order of preference.</summary>
    private static List<(Typeface, GlyphTypeface)> LoadFonts()
    {
        var fonts = new List<(Typeface, GlyphTypeface)>();
        var primary = new Typeface(Typography.Code);
        fonts.Add((primary, FontManager.Current.TryGetGlyphTypeface(primary, out var primaryGlyphs)
            ? primaryGlyphs
            : throw new InvalidOperationException("No usable font for the editor.")));

        foreach (var family in Typography.CodeFallbackFamilies)
        {
            // A missing family silently resolves to some default font; only keep exact matches.
            var typeface = new Typeface(new FontFamily(family));
            if (FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs)
                && string.Equals(glyphs.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                fonts.Add((typeface, glyphs));
            }
        }

        return fonts;
    }
}
