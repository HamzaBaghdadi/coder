using System.Globalization;

namespace Coder.Core.Svg;

/// <summary>Parsers for SVG attribute values: numbers, lengths, number lists and transform lists.</summary>
internal static class SvgValues
{
    public static bool TryNumber(string? text, out double value)
    {
        value = 0;
        return text is not null
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>Numbers separated by whitespace and/or commas, using the path grammar (so "1-2" is two numbers).</summary>
    public static List<double> NumberList(string? text)
    {
        var list = new List<double>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ','))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            if (text[i] is '+' or '-')
            {
                i++;
            }

            var digits = 0;
            var dot = false;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || (text[i] == '.' && !dot)))
            {
                dot |= text[i] == '.';
                digits += char.IsAsciiDigit(text[i]) ? 1 : 0;
                i++;
            }

            if (digits > 0 && i < text.Length && text[i] is 'e' or 'E')
            {
                var j = i + 1;
                if (j < text.Length && text[j] is '+' or '-')
                {
                    j++;
                }

                if (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    while (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        j++;
                    }

                    i = j;
                }
            }

            if (digits == 0 || !TryNumber(text[start..i], out var value))
            {
                break; // Malformed: keep what was read so far.
            }

            list.Add(value);
        }

        return list;
    }

    /// <summary>
    /// A length in user units. Absolute units convert at 96 dpi; em/ex use <paramref name="fontSize"/>;
    /// a percentage is a fraction of <paramref name="percentBase"/>.
    /// </summary>
    public static bool TryLength(string? text, double fontSize, double percentBase, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().ToLowerInvariant();
        var end = s.Length;
        while (end > 0 && (char.IsAsciiLetter(s[end - 1]) || s[end - 1] == '%'))
        {
            end--;
        }

        // An exponent ("1e3") is part of the number, not a unit.
        if (end < s.Length && s[end] == 'e' && end + 1 < s.Length && char.IsAsciiDigit(s[end + 1]))
        {
            return TryNumber(s, out value);
        }

        if (!TryNumber(s[..end], out var number))
        {
            return false;
        }

        double? factor = s[end..] switch
        {
            "" or "px" => 1,
            "pt" => 96.0 / 72,
            "pc" => 16,
            "in" => 96,
            "cm" => 96 / 2.54,
            "mm" => 96 / 25.4,
            "q" => 96 / 101.6,
            "em" => fontSize,
            "ex" => fontSize / 2,
            "rem" => 16,
            "%" => percentBase / 100,
            _ => null,
        };

        if (factor is null)
        {
            return false;
        }

        value = number * factor.Value;
        return double.IsFinite(value);
    }

    /// <summary>A transform list ("translate(10) rotate(45 5 5) ..."); returns false (identity) on any error.</summary>
    public static bool TryTransform(string? text, out SvgMatrix matrix)
    {
        matrix = SvgMatrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var result = SvgMatrix.Identity;
        var i = 0;
        while (true)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ','))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var open = text.IndexOf('(', i);
            var close = open < 0 ? -1 : text.IndexOf(')', open);
            if (close < 0)
            {
                return false;
            }

            var name = text[i..open].Trim().ToLowerInvariant();
            var args = NumberList(text[(open + 1)..close]);
            SvgMatrix? step = (name, args.Count) switch
            {
                ("matrix", 6) => new SvgMatrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                ("translate", 1) => SvgMatrix.Translate(args[0], 0),
                ("translate", 2) => SvgMatrix.Translate(args[0], args[1]),
                ("scale", 1) => SvgMatrix.Scale(args[0], args[0]),
                ("scale", 2) => SvgMatrix.Scale(args[0], args[1]),
                ("rotate", 1) => SvgMatrix.Rotate(args[0]),
                ("rotate", 3) => SvgMatrix.Translate(-args[1], -args[2])
                    .Then(SvgMatrix.Rotate(args[0]))
                    .Then(SvgMatrix.Translate(args[1], args[2])),
                ("skewx", 1) => SvgMatrix.SkewX(args[0]),
                ("skewy", 1) => SvgMatrix.SkewY(args[0]),
                _ => null,
            };

            if (step is null)
            {
                return false;
            }

            // In "A B", B applies to points first: the list composes right to left.
            result = step.Value.Then(result);
            i = close + 1;
        }

        matrix = result;
        return true;
    }
}
