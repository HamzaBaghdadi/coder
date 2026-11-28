using System.Globalization;

namespace Coder.Core.Svg;

/// <summary>Parses CSS colors: hex forms, rgb()/rgba(), hsl()/hsla() and the CSS named colors.</summary>
public static class SvgColors
{
    private const string NamedTable =
        "aliceblue:f0f8ff,antiquewhite:faebd7,aqua:00ffff,aquamarine:7fffd4,azure:f0ffff,beige:f5f5dc,bisque:ffe4c4," +
        "black:000000,blanchedalmond:ffebcd,blue:0000ff,blueviolet:8a2be2,brown:a52a2a,burlywood:deb887,cadetblue:5f9ea0," +
        "chartreuse:7fff00,chocolate:d2691e,coral:ff7f50,cornflowerblue:6495ed,cornsilk:fff8dc,crimson:dc143c,cyan:00ffff," +
        "darkblue:00008b,darkcyan:008b8b,darkgoldenrod:b8860b,darkgray:a9a9a9,darkgreen:006400,darkgrey:a9a9a9," +
        "darkkhaki:bdb76b,darkmagenta:8b008b,darkolivegreen:556b2f,darkorange:ff8c00,darkorchid:9932cc,darkred:8b0000," +
        "darksalmon:e9967a,darkseagreen:8fbc8f,darkslateblue:483d8b,darkslategray:2f4f4f,darkslategrey:2f4f4f," +
        "darkturquoise:00ced1,darkviolet:9400d3,deeppink:ff1493,deepskyblue:00bfff,dimgray:696969,dimgrey:696969," +
        "dodgerblue:1e90ff,firebrick:b22222,floralwhite:fffaf0,forestgreen:228b22,fuchsia:ff00ff,gainsboro:dcdcdc," +
        "ghostwhite:f8f8ff,gold:ffd700,goldenrod:daa520,gray:808080,green:008000,greenyellow:adff2f,grey:808080," +
        "honeydew:f0fff0,hotpink:ff69b4,indianred:cd5c5c,indigo:4b0082,ivory:fffff0,khaki:f0e68c,lavender:e6e6fa," +
        "lavenderblush:fff0f5,lawngreen:7cfc00,lemonchiffon:fffacd,lightblue:add8e6,lightcoral:f08080,lightcyan:e0ffff," +
        "lightgoldenrodyellow:fafad2,lightgray:d3d3d3,lightgreen:90ee90,lightgrey:d3d3d3,lightpink:ffb6c1," +
        "lightsalmon:ffa07a,lightseagreen:20b2aa,lightskyblue:87cefa,lightslategray:778899,lightslategrey:778899," +
        "lightsteelblue:b0c4de,lightyellow:ffffe0,lime:00ff00,limegreen:32cd32,linen:faf0e6,magenta:ff00ff,maroon:800000," +
        "mediumaquamarine:66cdaa,mediumblue:0000cd,mediumorchid:ba55d3,mediumpurple:9370db,mediumseagreen:3cb371," +
        "mediumslateblue:7b68ee,mediumspringgreen:00fa9a,mediumturquoise:48d1cc,mediumvioletred:c71585," +
        "midnightblue:191970,mintcream:f5fffa,mistyrose:ffe4e1,moccasin:ffe4b5,navajowhite:ffdead,navy:000080," +
        "oldlace:fdf5e6,olive:808000,olivedrab:6b8e23,orange:ffa500,orangered:ff4500,orchid:da70d6,palegoldenrod:eee8aa," +
        "palegreen:98fb98,paleturquoise:afeeee,palevioletred:db7093,papayawhip:ffefd5,peachpuff:ffdab9,peru:cd853f," +
        "pink:ffc0cb,plum:dda0dd,powderblue:b0e0e6,purple:800080,rebeccapurple:663399,red:ff0000,rosybrown:bc8f8f," +
        "royalblue:4169e1,saddlebrown:8b4513,salmon:fa8072,sandybrown:f4a460,seagreen:2e8b57,seashell:fff5ee," +
        "sienna:a0522d,silver:c0c0c0,skyblue:87ceeb,slateblue:6a5acd,slategray:708090,slategrey:708090,snow:fffafa," +
        "springgreen:00ff7f,steelblue:4682b4,tan:d2b48c,teal:008080,thistle:d8bfd8,tomato:ff6347,turquoise:40e0d0," +
        "violet:ee82ee,wheat:f5deb3,white:ffffff,whitesmoke:f5f5f5,yellow:ffff00,yellowgreen:9acd32";

    private static readonly Dictionary<string, SvgColor> Named = BuildNamed();

    /// <summary>Every CSS named color (lowercase), plus <c>transparent</c>.</summary>
    public static IReadOnlyCollection<string> Names => Named.Keys;

    /// <summary>
    /// Parses <paramref name="text"/> as a CSS color. Keywords such as <c>none</c> or <c>currentColor</c> are not
    /// colors and return false; the caller handles them.
    /// </summary>
    public static bool TryParse(string? text, out SvgColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value[0] == '#')
        {
            return TryParseHex(value.AsSpan(1), out color);
        }

        var open = value.IndexOf('(', StringComparison.Ordinal);
        if (open > 0)
        {
            if (!value.EndsWith(')'))
            {
                return false;
            }

            var name = value[..open].Trim().ToLowerInvariant();
            var args = SplitArguments(value[(open + 1)..^1]);
            return name switch
            {
                "rgb" or "rgba" => TryParseRgb(args, out color),
                "hsl" or "hsla" => TryParseHsl(args, out color),
                _ => false,
            };
        }

        return Named.TryGetValue(value.ToLowerInvariant(), out color);
    }

    private static Dictionary<string, SvgColor> BuildNamed()
    {
        var map = new Dictionary<string, SvgColor>(StringComparer.Ordinal) { ["transparent"] = SvgColor.Transparent };
        foreach (var entry in NamedTable.Split(','))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            _ = TryParseHex(entry.AsSpan(colon + 1), out var color);
            map[entry[..colon]] = color;
        }

        return map;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out SvgColor color)
    {
        color = default;
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        switch (hex.Length)
        {
            case 3:
            case 4:
                var r = Nibble(hex[0]);
                var g = Nibble(hex[1]);
                var b = Nibble(hex[2]);
                var a = hex.Length == 4 ? Nibble(hex[3]) : 15;
                color = new((byte)(r * 17), (byte)(g * 17), (byte)(b * 17), (byte)(a * 17));
                return true;
            case 6:
            case 8:
                color = new(
                    (byte)((Nibble(hex[0]) << 4) | Nibble(hex[1])),
                    (byte)((Nibble(hex[2]) << 4) | Nibble(hex[3])),
                    (byte)((Nibble(hex[4]) << 4) | Nibble(hex[5])),
                    hex.Length == 8 ? (byte)((Nibble(hex[6]) << 4) | Nibble(hex[7])) : (byte)255);
                return true;
            default:
                return false;
        }
    }

    private static int Nibble(char c) => c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a') + 10;

    /// <summary>Splits "1, 2, 3" or "1 2 3 / 50%" into ["1","2","3","50%"]; the slash marks the alpha.</summary>
    private static List<string> SplitArguments(string inner) =>
        [.. inner.Replace("/", " ", StringComparison.Ordinal)
            .Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)];

    private static bool TryParseRgb(List<string> args, out SvgColor color)
    {
        color = default;
        if (args.Count is not (3 or 4))
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[3];
        for (var i = 0; i < 3; i++)
        {
            var arg = args[i];
            if (arg.EndsWith('%'))
            {
                if (!TryNumber(arg[..^1], out var percent))
                {
                    return false;
                }

                channels[i] = ToByte(percent * 255 / 100);
            }
            else
            {
                if (!TryNumber(arg, out var value))
                {
                    return false;
                }

                channels[i] = ToByte(value);
            }
        }

        if (!TryAlpha(args, 3, out var alpha))
        {
            return false;
        }

        color = new(channels[0], channels[1], channels[2], alpha);
        return true;
    }

    private static bool TryParseHsl(List<string> args, out SvgColor color)
    {
        color = default;
        if (args.Count is not (3 or 4) || !TryHue(args[0], out var hue)
            || !args[1].EndsWith('%') || !TryNumber(args[1][..^1], out var saturation)
            || !args[2].EndsWith('%') || !TryNumber(args[2][..^1], out var lightness)
            || !TryAlpha(args, 3, out var alpha))
        {
            return false;
        }

        var h = (((hue % 360) + 360) % 360) / 360;
        var s = Math.Clamp(saturation / 100, 0, 1);
        var l = Math.Clamp(lightness / 100, 0, 1);
        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        color = new(
            ToByte(HueToChannel(p, q, h + (1.0 / 3)) * 255),
            ToByte(HueToChannel(p, q, h) * 255),
            ToByte(HueToChannel(p, q, h - (1.0 / 3)) * 255),
            alpha);
        return true;
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t < 1.0 / 6 ? p + ((q - p) * 6 * t)
            : t < 0.5 ? q
            : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6)
            : p;
    }

    private static bool TryHue(string text, out double degrees)
    {
        var lower = text.ToLowerInvariant();
        (string Suffix, double Factor)[] units = [("deg", 1), ("grad", 0.9), ("rad", 180 / Math.PI), ("turn", 360)];
        foreach (var (suffix, factor) in units)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal) && TryNumber(lower[..^suffix.Length], out var value))
            {
                degrees = value * factor;
                return true;
            }
        }

        return TryNumber(lower, out degrees);
    }

    private static bool TryAlpha(List<string> args, int index, out byte alpha)
    {
        alpha = 255;
        if (args.Count <= index)
        {
            return true;
        }

        var arg = args[index];
        var percent = arg.EndsWith('%');
        if (!TryNumber(percent ? arg[..^1] : arg, out var value))
        {
            return false;
        }

        alpha = ToByte(Math.Clamp(percent ? value / 100 : value, 0, 1) * 255);
        return true;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
