using System.Text;

namespace Coder.Core.Lsp;

/// <summary>
/// Turns an LSP snippet (<c>fmt.Println(${1:a ...any})$0</c>) into plain text plus where to put the
/// selection afterwards: the first tab stop (its placeholder selected), else <c>$0</c>, else the end.
/// <see cref="Expansion.TabStops"/> lists every stop for Tab navigation; a number used twice is a mirror.
/// </summary>
public static class Snippet
{
    /// <summary>A placeholder position in the expanded text; stops with the same number are edited together.</summary>
    public readonly record struct TabStop(int Number, int Start, int Length);

    public readonly record struct Expansion(string Text, int SelectionStart, int SelectionLength)
    {
        public IReadOnlyList<TabStop> TabStops { get; init; } = [];
    }

    public static Expansion Expand(string snippet)
    {
        var parser = new Parser(snippet);
        parser.Parse(stopAt: null);
        var stops = FillMirrors(parser.Output, parser.TabStops);
        var target = stops.Where(s => s.Number > 0).OrderBy(s => s.Number).ThenBy(s => s.Start).Cast<TabStop?>().FirstOrDefault()
                     ?? stops.Where(s => s.Number == 0).Cast<TabStop?>().FirstOrDefault();
        var text = parser.Output.ToString();
        var expansion = target is { } stop ? new Expansion(text, stop.Start, stop.Length) : new Expansion(text, text.Length, 0);
        return expansion with { TabStops = stops };
    }

    /// <summary>
    /// A bare <c>$1</c> after <c>${1:i}</c> mirrors it: the placeholder's text is copied into every empty
    /// stop with that number, shifting the stops after it.
    /// </summary>
    private static List<TabStop> FillMirrors(StringBuilder output, List<TabStop> stops)
    {
        var placeholders = stops.Where(s => s.Length > 0).GroupBy(s => s.Number)
            .ToDictionary(g => g.Key, g => output.ToString(g.First().Start, g.First().Length));
        var result = stops.ToList();
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var stop = result[i];
            if (stop.Length > 0 || !placeholders.TryGetValue(stop.Number, out var text))
            {
                continue;
            }

            output.Insert(stop.Start, text);
            result[i] = stop with { Length = text.Length };
            for (var j = 0; j < result.Count; j++)
            {
                if (j != i && result[j].Start >= stop.Start && !(result[j].Start == stop.Start && j < i))
                {
                    result[j] = result[j] with { Start = result[j].Start + text.Length };
                }
            }
        }

        return result;
    }

    private sealed class Parser(string source)
    {
        private int _index;

        public StringBuilder Output { get; } = new();

        public List<TabStop> TabStops { get; } = [];

        /// <summary>Copies text until <paramref name="stopAt"/> (unescaped) or the end.</summary>
        public void Parse(char? stopAt)
        {
            while (_index < source.Length)
            {
                var c = source[_index];
                if (c == stopAt)
                {
                    return;
                }

                if (c == '\\' && _index + 1 < source.Length && source[_index + 1] is '$' or '}' or '\\' or ',' or '|')
                {
                    Output.Append(source[_index + 1]);
                    _index += 2;
                }
                else if (c == '$' && TryParseDollar())
                {
                    continue;
                }
                else
                {
                    Output.Append(c);
                    _index++;
                }
            }
        }

        /// <summary><c>$1</c>, <c>${1}</c>, <c>${1:placeholder}</c>, <c>${1|a,b|}</c>, <c>$VAR</c>, <c>${VAR:default}</c>.</summary>
        private bool TryParseDollar()
        {
            var start = _index;
            _index++; // $
            if (_index < source.Length && char.IsAsciiDigit(source[_index]))
            {
                TabStops.Add(new TabStop(ReadNumber(), Output.Length, 0));
                return true;
            }

            if (_index < source.Length && (char.IsAsciiLetter(source[_index]) || source[_index] == '_'))
            {
                ReadName(); // variables (TM_FILENAME...) are not supported: they expand to nothing
                return true;
            }

            if (_index >= source.Length || source[_index] != '{')
            {
                _index = start;
                return false;
            }

            _index++; // {
            if (_index < source.Length && char.IsAsciiDigit(source[_index]))
            {
                var number = ReadNumber();
                var outputStart = Output.Length;
                if (_index < source.Length && source[_index] == ':')
                {
                    _index++;
                    Parse(stopAt: '}');
                }
                else if (_index < source.Length && source[_index] == '|')
                {
                    _index++;
                    var choices = new StringBuilder();
                    while (_index < source.Length && source[_index] != '|')
                    {
                        choices.Append(source[_index++]);
                    }

                    Output.Append(choices.ToString().Split(',')[0]);
                    _index++; // closing |
                }

                SkipClosingBrace();
                TabStops.Add(new TabStop(number, outputStart, Output.Length - outputStart));
                return true;
            }

            ReadName();
            if (_index < source.Length && source[_index] == ':')
            {
                _index++;
                Parse(stopAt: '}'); // a variable's default text
            }
            else
            {
                while (_index < source.Length && source[_index] != '}')
                {
                    _index++; // a transform: unsupported, dropped
                }
            }

            SkipClosingBrace();
            return true;
        }

        private int ReadNumber()
        {
            var number = 0;
            while (_index < source.Length && char.IsAsciiDigit(source[_index]))
            {
                number = number * 10 + (source[_index++] - '0');
            }

            return number;
        }

        private void ReadName()
        {
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_'))
            {
                _index++;
            }
        }

        private void SkipClosingBrace()
        {
            if (_index < source.Length && source[_index] == '}')
            {
                _index++;
            }
        }
    }
}
