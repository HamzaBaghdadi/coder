using System.Globalization;
using System.Text;

namespace Coder.Core.Svg;

/// <summary>
/// SVG path data helpers: builders that turn basic shapes into path data, and a tolerant normalizer that
/// rewrites any <c>d</c> attribute into plain absolute commands (one letter per segment, space-separated
/// invariant numbers), which every path parser reads the same way.
/// </summary>
public static class SvgPathData
{
    private const string Commands = "MmZzLlHhVvCcSsQqTtAa";

    /// <summary>A number as written into generated path data: invariant, at most 6 decimals, no exponent.</summary>
    public static string Format(double value)
    {
        var rounded = Math.Round(value, 6);
        return (rounded == 0 ? 0 : rounded).ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>A rectangle, with corners rounded by <paramref name="rx"/>/<paramref name="ry"/> (0 for square).</summary>
    public static string Rectangle(double x, double y, double width, double height, double rx = 0, double ry = 0)
    {
        if (!(width > 0) || !(height > 0))
        {
            return "";
        }

        rx = Math.Min(Math.Max(rx, 0), width / 2);
        ry = Math.Min(Math.Max(ry, 0), height / 2);
        if (rx == 0 || ry == 0)
        {
            return Join("M", x, y, "H", x + width, "V", y + height, "H", x, "Z");
        }

        var r = $"{Format(rx)} {Format(ry)} 0 0 1";
        return Join("M", x + rx, y, "H", x + width - rx, "A", r, x + width, y + ry,
            "V", y + height - ry, "A", r, x + width - rx, y + height,
            "H", x + rx, "A", r, x, y + height - ry,
            "V", y + ry, "A", r, x + rx, y, "Z");
    }

    public static string Ellipse(double cx, double cy, double rx, double ry)
    {
        if (!(rx > 0) || !(ry > 0))
        {
            return "";
        }

        // Four quarter arcs: no arc is ever exactly half a turn, so no parser has to guess its direction.
        var r = $"{Format(rx)} {Format(ry)} 0 0 1";
        return Join("M", cx + rx, cy, "A", r, cx, cy + ry, "A", r, cx - rx, cy, "A", r, cx, cy - ry, "A", r, cx + rx, cy, "Z");
    }

    public static string Line(double x1, double y1, double x2, double y2) => Join("M", x1, y1, "L", x2, y2);

    /// <summary>A polyline (or, when <paramref name="close"/>, a polygon) through x,y pairs; an odd last value is dropped.</summary>
    public static string Polyline(IReadOnlyList<double> points, bool close)
    {
        if (points.Count < 2)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (var i = 0; i + 1 < points.Count; i += 2)
        {
            builder.Append(i == 0 ? "M " : " L ").Append(Format(points[i])).Append(' ').Append(Format(points[i + 1]));
        }

        if (close)
        {
            builder.Append(" Z");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rewrites path data into absolute commands. As the SVG spec requires, parsing stops at the first error and
    /// everything before it is kept; the result is empty when nothing usable precedes the error (or the data does
    /// not start with a moveto). Zero-radius arcs become lines, as the spec says.
    /// </summary>
    public static string Normalize(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return "";
        }

        var output = new StringBuilder();
        var reader = new Reader(data);
        double x = 0, y = 0, startX = 0, startY = 0;
        var command = '\0';
        var first = true;
        Span<double> args = stackalloc double[7];

        while (true)
        {
            reader.SkipSeparators();
            if (reader.AtEnd)
            {
                break;
            }

            var next = reader.Peek;
            if (Commands.Contains(next, StringComparison.Ordinal))
            {
                command = next;
                reader.Advance();
            }
            else if (command == '\0' || command is 'Z' or 'z')
            {
                break; // A number with no command to repeat.
            }
            else if (command == 'M')
            {
                command = 'L'; // Extra pairs after a moveto are linetos.
            }
            else if (command == 'm')
            {
                command = 'l';
            }

            if (first && command is not ('M' or 'm'))
            {
                return "";
            }

            first = false;
            var upper = char.ToUpperInvariant(command);
            var relative = command != upper;
            var count = upper switch
            {
                'Z' => 0,
                'H' or 'V' => 1,
                'M' or 'L' or 'T' => 2,
                'S' or 'Q' => 4,
                'C' => 6,
                _ => 7, // A
            };

            var ok = true;
            for (var i = 0; i < count && ok; i++)
            {
                ok = upper == 'A' && (i is 3 or 4) ? reader.TryReadFlag(out args[i]) : reader.TryReadNumber(out args[i]);
            }

            if (!ok)
            {
                break;
            }

            var dx = relative ? x : 0;
            var dy = relative ? y : 0;
            switch (upper)
            {
                case 'Z':
                    output.Append(" Z");
                    x = startX;
                    y = startY;
                    break;
                case 'M':
                    x = args[0] + dx;
                    y = args[1] + dy;
                    startX = x;
                    startY = y;
                    Append(output, "M", x, y);
                    break;
                case 'L':
                case 'T':
                    x = args[0] + dx;
                    y = args[1] + dy;
                    Append(output, upper == 'L' ? "L" : "T", x, y);
                    break;
                case 'H':
                    x = args[0] + dx;
                    Append(output, "L", x, y);
                    break;
                case 'V':
                    y = args[0] + dy;
                    Append(output, "L", x, y);
                    break;
                case 'S':
                case 'Q':
                    Append(output, upper == 'S' ? "S" : "Q", args[0] + dx, args[1] + dy, args[2] + dx, args[3] + dy);
                    x = args[2] + dx;
                    y = args[3] + dy;
                    break;
                case 'C':
                    Append(output, "C", args[0] + dx, args[1] + dy, args[2] + dx, args[3] + dy, args[4] + dx, args[5] + dy);
                    x = args[4] + dx;
                    y = args[5] + dy;
                    break;
                default:
                    var endX = args[5] + dx;
                    var endY = args[6] + dy;
                    if (endX == x && endY == y)
                    {
                        break; // An arc to its own start draws nothing.
                    }

                    if (args[0] == 0 || args[1] == 0)
                    {
                        Append(output, "L", endX, endY);
                    }
                    else
                    {
                        Append(output, "A", Math.Abs(args[0]), Math.Abs(args[1]), args[2], args[3], args[4], endX, endY);
                    }

                    x = endX;
                    y = endY;
                    break;
            }
        }

        return output.ToString().TrimStart();
    }

    private static void Append(StringBuilder output, string command, params ReadOnlySpan<double> values)
    {
        output.Append(' ').Append(command);
        foreach (var value in values)
        {
            output.Append(' ').Append(Format(value));
        }
    }

    private static string Join(params object[] parts) =>
        string.Join(' ', parts.Select(p => p is double d ? Format(d) : p.ToString()));

    /// <summary>A cursor over path data following the SVG grammar (compact numbers like "1.5.5" and "1-2").</summary>
    private ref struct Reader(string text)
    {
        private readonly string _text = text;
        private int _position;

        public readonly bool AtEnd => _position >= _text.Length;

        public readonly char Peek => _text[_position];

        public void Advance() => _position++;

        public void SkipSeparators()
        {
            while (!AtEnd && (char.IsWhiteSpace(Peek) || Peek == ','))
            {
                _position++;
            }
        }

        public bool TryReadFlag(out double value)
        {
            SkipSeparators();
            value = 0;
            if (AtEnd || Peek is not ('0' or '1'))
            {
                return false;
            }

            value = Peek - '0';
            _position++;
            return true;
        }

        public bool TryReadNumber(out double value)
        {
            SkipSeparators();
            value = 0;
            var start = _position;
            var i = _position;
            if (i < _text.Length && _text[i] is '+' or '-')
            {
                i++;
            }

            var digits = 0;
            while (i < _text.Length && char.IsAsciiDigit(_text[i]))
            {
                i++;
                digits++;
            }

            if (i < _text.Length && _text[i] == '.')
            {
                i++;
                while (i < _text.Length && char.IsAsciiDigit(_text[i]))
                {
                    i++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                return false;
            }

            if (i < _text.Length && _text[i] is 'e' or 'E')
            {
                var j = i + 1;
                if (j < _text.Length && _text[j] is '+' or '-')
                {
                    j++;
                }

                if (j < _text.Length && char.IsAsciiDigit(_text[j]))
                {
                    while (j < _text.Length && char.IsAsciiDigit(_text[j]))
                    {
                        j++;
                    }

                    i = j;
                }
            }

            if (!double.TryParse(_text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || !double.IsFinite(value))
            {
                return false;
            }

            _position = i;
            return true;
        }
    }
}
