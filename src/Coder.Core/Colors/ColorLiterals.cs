using System.Globalization;
using System.Text;

namespace Coder.Core.Colors;

/// <summary>The notations a color literal can be written in; the picker's header cycles through them.</summary>
public enum ColorFormat
{
    Hex,
    Rgb,
    Hsl,
}

/// <summary>
/// How a literal is written, so a changed color is written back the same way: hex digit count and case,
/// alpha first (XAML's #AARRGGBB) or last (CSS's #RRGGBBAA), the function's name and separators.
/// </summary>
public sealed record ColorStyle
{
    public ColorFormat Format { get; init; }

    /// <summary>Hex digits as written: 3, 4, 6 or 8.</summary>
    public int HexDigits { get; init; } = 6;

    public bool UpperCase { get; init; }

    /// <summary>#AARRGGBB and #ARGB (XAML, Android, Avalonia's Color.Parse) rather than CSS's #RRGGBBAA.</summary>
    public bool AlphaFirst { get; init; }

    /// <summary>"rgb", "rgba", "hsl" or "hsla" as written (case kept).</summary>
    public string FunctionName { get; init; } = "rgb";

    /// <summary>rgb(1, 2, 3) rather than rgb(1 2 3 / 50%).</summary>
    public bool Commas { get; init; } = true;

    public bool SpaceAfterComma { get; init; } = true;

    public bool AlphaPercent { get; init; }
}

/// <summary>A color written in a line: where it is (columns), its value and how it is written.</summary>
public readonly record struct ColorLiteral(int Start, int Length, RgbaColor Color, ColorStyle Style)
{
    public int End => Start + Length;
}

/// <summary>Language rules for finding colors.</summary>
/// <param name="CssSelectors">CSS-like: "#bad {" is an id selector, not a color.</param>
/// <param name="HexAlphaFirst">8 and 4 digit hex put alpha first (C#, XAML).</param>
/// <param name="SkipDigitOnlyHex">"#123" is an issue number, not a color (Markdown).</param>
public readonly record struct ColorSyntax(bool CssSelectors = false, bool HexAlphaFirst = false, bool SkipDigitOnlyHex = false);

/// <summary>
/// Finds, parses and writes color literals: #rgb, #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() and hsl()/hsla()
/// (comma or space syntax, percentages, alpha). Works on one line at a time so callers can cache per line.
/// </summary>
public static class ColorLiterals
{
    private static readonly System.Buffers.SearchValues<char> Digits = System.Buffers.SearchValues.Create("0123456789");
    private static readonly System.Buffers.SearchValues<char> UpperHex = System.Buffers.SearchValues.Create("ABCDEF");
    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("abcdef");

    private static readonly HashSet<string> CssLanguages = new(StringComparer.OrdinalIgnoreCase) { "css", "scss", "less", "sass", "stylus" };

    private static readonly HashSet<string> Enabled = new(StringComparer.OrdinalIgnoreCase)
    {
        "css", "scss", "less", "sass", "stylus", "html", "javascript", "typescript", "javascriptreact", "typescriptreact",
        "vue", "svelte", "json", "jsonc", "xml", "svg", "xaml", "axaml", "markdown", "csharp",
    };

    private static readonly HashSet<string> AlphaFirstLanguages = new(StringComparer.OrdinalIgnoreCase) { "csharp", "xaml", "axaml" };

    /// <summary>Whether colors are decorated in documents of this language.</summary>
    public static bool IsEnabledFor(string languageId) => Enabled.Contains(languageId);

    public static ColorSyntax SyntaxFor(string languageId) => new(
        CssSelectors: CssLanguages.Contains(languageId),
        HexAlphaFirst: AlphaFirstLanguages.Contains(languageId),
        SkipDigitOnlyHex: string.Equals(languageId, "markdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds every color literal of <paramref name="line"/> to <paramref name="results"/>, in order.</summary>
    public static void Find(string line, ColorSyntax syntax, List<ColorLiteral> results)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '#')
            {
                if (TryHexAt(line, i, syntax, out var hex))
                {
                    results.Add(hex);
                    i = hex.End - 1;
                }
            }
            else if (c is 'r' or 'R' or 'h' or 'H' && (i == 0 || !IsWordChar(line[i - 1])) && TryFunctionAt(line, i, out var function))
            {
                results.Add(function);
                i = function.End - 1;
            }
        }
    }

    /// <summary>Parses a whole string as one color (the picker's text field); surrounding spaces are ignored.</summary>
    public static bool TryParse(string text, ColorSyntax syntax, out ColorLiteral literal)
    {
        var trimmed = text.Trim();
        var found = new List<ColorLiteral>(1);
        Find(trimmed, syntax with { CssSelectors = false, SkipDigitOnlyHex = false }, found);
        if (found is [{ Start: 0 } only] && only.Length == trimmed.Length)
        {
            literal = only;
            return true;
        }

        literal = default;
        return false;
    }

    /// <summary>Writes <paramref name="color"/> the way <paramref name="style"/> says.</summary>
    public static string Format(RgbaColor color, ColorStyle style) => style.Format switch
    {
        ColorFormat.Rgb => FormatRgb(color, style),
        ColorFormat.Hsl => FormatHsl(color, style),
        _ => FormatHex(color, style),
    };

    /// <summary>
    /// The style for writing in <paramref name="format"/> after switching from <paramref name="current"/>:
    /// keeps the hex case and alpha order, and the comma or space syntax of functions.
    /// </summary>
    public static ColorStyle Switch(ColorStyle current, ColorFormat format)
    {
        if (format == current.Format)
        {
            return current;
        }

        return current with
        {
            Format = format,
            HexDigits = current.Format == ColorFormat.Hex ? current.HexDigits : 6,
            FunctionName = format switch
            {
                ColorFormat.Rgb => "rgb",
                ColorFormat.Hsl => "hsl",
                _ => current.FunctionName,
            },
        };
    }

    /// <summary>The next format in the header's cycle: hex, rgb, hsl, hex…</summary>
    public static ColorFormat Next(ColorFormat format) => format switch
    {
        ColorFormat.Hex => ColorFormat.Rgb,
        ColorFormat.Rgb => ColorFormat.Hsl,
        _ => ColorFormat.Hex,
    };

    // Hex --------------------------------------------------------------------------------------------

    private static bool TryHexAt(string line, int at, ColorSyntax syntax, out ColorLiteral literal)
    {
        literal = default;

        // Bounded by non-word characters: not "a#fff", not "&#123;" (an HTML character reference), not "#fff0x".
        if (at > 0 && (IsWordChar(line[at - 1]) || line[at - 1] is '&' or '#'))
        {
            return false;
        }

        var end = at + 1;
        while (end < line.Length && char.IsAsciiHexDigit(line[end]))
        {
            end++;
        }

        var digits = end - at - 1;
        if (digits is not (3 or 4 or 6 or 8) || (end < line.Length && (IsWordChar(line[end]) || line[end] == '-')))
        {
            return false;
        }

        var span = line.AsSpan(at + 1, digits);
        if (syntax.SkipDigitOnlyHex && !span.ContainsAnyExcept(Digits))
        {
            return false;
        }

        if (syntax.CssSelectors && IsSelector(line, end))
        {
            return false;
        }

        literal = new ColorLiteral(at, digits + 1, ParseHex(span, syntax.HexAlphaFirst), new ColorStyle
        {
            Format = ColorFormat.Hex,
            HexDigits = digits,
            UpperCase = span.ContainsAny(UpperHex) && !span.ContainsAny(LowerHex),
            AlphaFirst = syntax.HexAlphaFirst,
        });
        return true;
    }

    /// <summary>"#bad {" or "#fed, .a {": an id selector, a rule body opens after it before any declaration ends.</summary>
    private static bool IsSelector(string line, int end)
    {
        var rest = line.AsSpan(end);
        var brace = rest.IndexOf('{');
        var semicolon = rest.IndexOf(';');
        return brace >= 0 && (semicolon < 0 || brace < semicolon);
    }

    private static RgbaColor ParseHex(ReadOnlySpan<char> digits, bool alphaFirst)
    {
        static byte Short(char c) => (byte)(Convert.ToInt32(c.ToString(), 16) * 17);
        static byte Long(ReadOnlySpan<char> pair) => byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return digits.Length switch
        {
            3 => new RgbaColor(Short(digits[0]), Short(digits[1]), Short(digits[2])),
            4 when alphaFirst => new RgbaColor(Short(digits[1]), Short(digits[2]), Short(digits[3]), Short(digits[0])),
            4 => new RgbaColor(Short(digits[0]), Short(digits[1]), Short(digits[2]), Short(digits[3])),
            6 => new RgbaColor(Long(digits[..2]), Long(digits[2..4]), Long(digits[4..6])),
            _ when alphaFirst => new RgbaColor(Long(digits[2..4]), Long(digits[4..6]), Long(digits[6..8]), Long(digits[..2])),
            _ => new RgbaColor(Long(digits[..2]), Long(digits[2..4]), Long(digits[4..6]), Long(digits[6..8])),
        };
    }

    private static string FormatHex(RgbaColor color, ColorStyle style)
    {
        static bool Shortens(byte value) => value % 17 == 0;
        var opaque = color.A == 255;
        var canShorten = Shortens(color.R) && Shortens(color.G) && Shortens(color.B) && Shortens(color.A);
        var hasAlpha = style.HexDigits is 4 or 8 || !opaque;
        var digits = style.HexDigits is 3 or 4 && canShorten ? (hasAlpha ? 4 : 3) : (hasAlpha ? 8 : 6);

        var builder = new StringBuilder("#", 9);
        var format = style.UpperCase ? "X" : "x";
        void Channel(byte value) => builder.Append(digits <= 4 ? (value / 17).ToString(format, CultureInfo.InvariantCulture) : value.ToString(format + "2", CultureInfo.InvariantCulture));

        if (hasAlpha && style.AlphaFirst)
        {
            Channel(color.A);
        }

        Channel(color.R);
        Channel(color.G);
        Channel(color.B);
        if (hasAlpha && !style.AlphaFirst)
        {
            Channel(color.A);
        }

        return builder.ToString();
    }

    // Functions --------------------------------------------------------------------------------------

    private static bool TryFunctionAt(string line, int at, out ColorLiteral literal)
    {
        literal = default;
        var nameEnd = at;
        while (nameEnd < line.Length && char.IsAsciiLetter(line[nameEnd]) && nameEnd - at < 4)
        {
            nameEnd++;
        }

        var name = line[at..nameEnd];
        var lower = name.ToLowerInvariant();
        if (lower is not ("rgb" or "rgba" or "hsl" or "hsla") || nameEnd >= line.Length || line[nameEnd] != '(')
        {
            return false;
        }

        var close = line.IndexOf(')', nameEnd);
        if (close < 0 || close - nameEnd > 64)
        {
            return false;
        }

        var inner = line[(nameEnd + 1)..close];
        if (!TryArguments(inner, out var channels, out var alpha, out var commas, out var alphaPercent))
        {
            return false;
        }

        var isHsl = lower.StartsWith('h');
        RgbaColor? color = isHsl ? HslOf(channels, alpha) : RgbOf(channels, alpha);
        if (color is not { } value)
        {
            return false;
        }

        var commaAt = inner.IndexOf(',', StringComparison.Ordinal);
        literal = new ColorLiteral(at, close + 1 - at, value, new ColorStyle
        {
            Format = isHsl ? ColorFormat.Hsl : ColorFormat.Rgb,
            FunctionName = name,
            Commas = commas,
            SpaceAfterComma = commaAt < 0 || (commaAt + 1 < inner.Length && inner[commaAt + 1] == ' '),
            AlphaPercent = alphaPercent,
        });
        return true;
    }

    /// <summary>"1, 2, 3", "1, 2, 3, 0.5", "1 2 3" or "1 2 3 / 50%" → three channels and an optional alpha.</summary>
    private static bool TryArguments(string inner, out string[] channels, out string? alpha, out bool commas, out bool alphaPercent)
    {
        alpha = null;
        alphaPercent = false;
        commas = inner.Contains(',', StringComparison.Ordinal);
        string[] parts;
        if (commas)
        {
            parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4)
            {
                alpha = parts[3];
                parts = parts[..3];
            }
        }
        else
        {
            var slash = inner.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                alpha = inner[(slash + 1)..].Trim();
                inner = inner[..slash];
            }

            parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        channels = parts;
        if (alpha is not null)
        {
            alphaPercent = alpha.EndsWith('%');
        }

        return parts.Length == 3 && parts.All(p => p.Length > 0) && alpha is not "";
    }

    private static RgbaColor? RgbOf(string[] channels, string? alpha)
    {
        var bytes = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!TryNumber(channels[i], out var value, out var percent))
            {
                return null;
            }

            bytes[i] = RgbaColor.ToByte(percent ? value / 100 : value / 255);
        }

        return AlphaOf(alpha) is { } a ? new RgbaColor(bytes[0], bytes[1], bytes[2], a) : null;
    }

    private static RgbaColor? HslOf(string[] channels, string? alpha)
    {
        if (!TryHue(channels[0], out var hue)
            || !TryNumber(channels[1], out var saturation, out _)
            || !TryNumber(channels[2], out var lightness, out _)
            || AlphaOf(alpha) is not { } a)
        {
            return null;
        }

        return new HslColor(hue, Math.Clamp(saturation / 100, 0, 1), Math.Clamp(lightness / 100, 0, 1), a / 255.0).ToRgba();
    }

    private static byte? AlphaOf(string? alpha)
    {
        if (alpha is null)
        {
            return 255;
        }

        return TryNumber(alpha, out var value, out var percent) ? RgbaColor.ToByte(percent ? value / 100 : value) : null;
    }

    private static bool TryHue(string text, out double degrees)
    {
        var (unit, scale) = text.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ? (3, 1.0)
            : text.EndsWith("turn", StringComparison.OrdinalIgnoreCase) ? (4, 360.0)
            : text.EndsWith("grad", StringComparison.OrdinalIgnoreCase) ? (4, 0.9)
            : text.EndsWith("rad", StringComparison.OrdinalIgnoreCase) ? (3, 180 / Math.PI)
            : (0, 1.0);
        var ok = double.TryParse(text.AsSpan(0, text.Length - unit), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out degrees);
        degrees *= scale;
        return ok;
    }

    /// <summary>A plain number or a percentage ("50%"); no exponents, no units.</summary>
    private static bool TryNumber(string text, out double value, out bool percent)
    {
        percent = text.EndsWith('%');
        var number = percent ? text.AsSpan(0, text.Length - 1) : text.AsSpan();
        return double.TryParse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static string FormatRgb(RgbaColor color, ColorStyle style) =>
        Function(style, color, [color.R.ToString(CultureInfo.InvariantCulture), color.G.ToString(CultureInfo.InvariantCulture), color.B.ToString(CultureInfo.InvariantCulture)]);

    private static string FormatHsl(RgbaColor color, ColorStyle style)
    {
        var hsl = color.ToHsl();
        return Function(style, color,
        [
            ((int)Math.Round(hsl.H) % 360).ToString(CultureInfo.InvariantCulture),
            Math.Round(hsl.S * 100).ToString(CultureInfo.InvariantCulture) + "%",
            Math.Round(hsl.L * 100).ToString(CultureInfo.InvariantCulture) + "%",
        ]);
    }

    /// <summary>rgb(…)/rgba(…) or hsl(…)/hsla(…) with the original separators; alpha only when not opaque.</summary>
    private static string Function(ColorStyle style, RgbaColor color, string[] channels)
    {
        var baseName = style.FunctionName.TrimEnd('a', 'A');
        if (baseName.Length != 3)
        {
            baseName = style.Format == ColorFormat.Hsl ? "hsl" : "rgb";
        }

        var hasAlpha = color.A != 255;
        var alpha = style.AlphaPercent
            ? Math.Round(color.Opacity * 100).ToString(CultureInfo.InvariantCulture) + "%"
            : Math.Round(color.Opacity, 2).ToString("0.##", CultureInfo.InvariantCulture);
        if (style.Commas)
        {
            // Legacy syntax: rgba() when there is alpha (or the original said rgba), rgb() otherwise.
            var keepsA = style.FunctionName.Length == 4;
            var name = hasAlpha || keepsA ? baseName + (char.IsUpper(baseName[0]) ? "A" : "a") : baseName;
            var separator = style.SpaceAfterComma ? ", " : ",";
            var values = hasAlpha || keepsA ? [.. channels, alpha] : channels;
            return $"{name}({string.Join(separator, values)})";
        }

        return hasAlpha ? $"{baseName}({string.Join(' ', channels)} / {alpha})" : $"{baseName}({string.Join(' ', channels)})";
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
