using System.Text;
using System.Text.RegularExpressions;

namespace Coder.Core.Lsp;

/// <summary>
/// Turns an LSP snippet (<c>fmt.Println(${1:a ...any})$0</c>) into plain text plus where to put the
/// selection afterwards: the first tab stop (its placeholder selected), else <c>$0</c>, else the end.
/// <see cref="Expansion.TabStops"/> lists every stop for Tab navigation; a number used twice is a mirror.
/// <para>Variables (<c>$TM_FILENAME</c>, <c>${CURRENT_YEAR}</c>, <c>${TM_FILENAME_BASE/(.*)/${1:/upcase}/}</c>)
/// take their value from the resolver when one is given, as in VS Code; without one they expand to their default.</para>
/// </summary>
public static class Snippet
{
    /// <summary>A placeholder position in the expanded text; stops with the same number are edited together.</summary>
    public readonly record struct TabStop(int Number, int Start, int Length);

    public readonly record struct Expansion(string Text, int SelectionStart, int SelectionLength)
    {
        public IReadOnlyList<TabStop> TabStops { get; init; } = [];
    }

    /// <param name="variables">A variable's value by name; null for a name it doesn't know (then the name is inserted, like VS Code).</param>
    public static Expansion Expand(string snippet, Func<string, string?>? variables = null)
    {
        var parser = new Parser(snippet, variables);
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

    private sealed class Parser(string source, Func<string, string?>? variables)
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
                Output.Append(Value(ReadName()) ?? string.Empty);
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

            var name = ReadName();
            if (name.Length == 0)
            {
                _index = start;
                return false;
            }

            if (_index < source.Length && source[_index] == ':')
            {
                // The default is used when the variable is unknown or empty: parse it, then drop it if not needed.
                _index++;
                var outputStart = Output.Length;
                var stopCount = TabStops.Count;
                Parse(stopAt: '}');
                if (variables?.Invoke(name) is { Length: > 0 } known)
                {
                    Output.Length = outputStart;
                    TabStops.RemoveRange(stopCount, TabStops.Count - stopCount);
                    Output.Append(known);
                }
            }
            else if (_index < source.Length && source[_index] == '/')
            {
                _index++;
                var pattern = ReadUntilSlash();
                var format = ReadUntilSlash();
                var options = new StringBuilder();
                while (_index < source.Length && source[_index] != '}')
                {
                    options.Append(source[_index++]);
                }

                Output.Append(Transform(variables?.Invoke(name) ?? string.Empty, pattern, format, options.ToString()));
            }
            else
            {
                Output.Append(Value(name) ?? string.Empty);
                while (_index < source.Length && source[_index] != '}')
                {
                    _index++; // unknown syntax: dropped
                }
            }

            SkipClosingBrace();
            return true;
        }

        /// <summary>
        /// What a variable without a default inserts: its value, or (with a resolver) its own name when the
        /// resolver doesn't know it; nothing without a resolver.
        /// </summary>
        private string? Value(string name) => variables is null ? null : variables(name) ?? name;

        /// <summary>
        /// A transform's regex or format, up to the next unescaped slash (consumed); <c>\/</c> is a slash, and
        /// slashes inside the format's <c>${1:/upcase}</c> don't count.
        /// </summary>
        private string ReadUntilSlash()
        {
            var text = new StringBuilder();
            var depth = 0;
            while (_index < source.Length && (source[_index] != '/' || depth > 0))
            {
                if (source[_index] == '$' && _index + 1 < source.Length && source[_index + 1] == '{')
                {
                    depth++;
                }
                else if (source[_index] == '}' && depth > 0)
                {
                    depth--;
                }

                if (source[_index] == '\\' && _index + 1 < source.Length)
                {
                    if (source[_index + 1] != '/')
                    {
                        text.Append('\\');
                    }

                    _index++;
                }

                text.Append(source[_index++]);
            }

            _index++; // the slash
            return text.ToString();
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

        private string ReadName()
        {
            var start = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_'))
            {
                _index++;
            }

            return source[start.._index];
        }

        private void SkipClosingBrace()
        {
            if (_index < source.Length && source[_index] == '}')
            {
                _index++;
            }
        }
    }

    /// <summary>
    /// A variable transform (<c>/(.*)/${1:/upcase}/g</c>): each match of the regex (the first, or all with
    /// <c>g</c>) is replaced by the format, which may use groups, case changes and conditionals.
    /// </summary>
    internal static string Transform(string value, string pattern, string format, string options)
    {
        Regex regex;
        try
        {
            var regexOptions = (options.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None)
                               | (options.Contains('m') ? RegexOptions.Multiline : RegexOptions.None)
                               | (options.Contains('s') ? RegexOptions.Singleline : RegexOptions.None);
            regex = new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return value; // not a regex .NET can read
        }

        try
        {
            return regex.Replace(value, m => Format(format, m), options.Contains('g') ? -1 : 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return value;
        }
    }

    /// <summary><c>$1</c>, <c>${1}</c>, <c>${1:/upcase}</c>, <c>${1:+if}</c>, <c>${1:?if:else}</c>, <c>${1:-else}</c>, <c>${1:else}</c>.</summary>
    private static string Format(string format, Match match)
    {
        var output = new StringBuilder();
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                output.Append(format[i + 1] switch { 'n' => '\n', 't' => '\t', var other => other });
                i += 2;
            }
            else if (c == '$' && i + 1 < format.Length && char.IsAsciiDigit(format[i + 1]))
            {
                i++;
                output.Append(Group(match, ReadNumber(format, ref i)));
            }
            else if (c == '$' && i + 2 < format.Length && format[i + 1] == '{' && char.IsAsciiDigit(format[i + 2]))
            {
                i += 2;
                var group = Group(match, ReadNumber(format, ref i));
                var body = i < format.Length && format[i] == ':' ? ReadBraced(format, ref i) : null;
                if (body is null && i < format.Length && format[i] == '}')
                {
                    i++;
                }

                output.Append(body switch
                {
                    null => group,
                    "/upcase" => group.ToUpperInvariant(),
                    "/downcase" => group.ToLowerInvariant(),
                    "/capitalize" => group.Length == 0 ? group : char.ToUpperInvariant(group[0]) + group[1..],
                    "/pascalcase" => Case(group, pascal: true),
                    "/camelcase" => Case(group, pascal: false),
                    _ when body.StartsWith('+') => group.Length > 0 ? body[1..] : string.Empty,
                    _ when body.StartsWith('?') => Conditional(body[1..], group.Length > 0),
                    _ when body.StartsWith('-') => group.Length > 0 ? group : body[1..],
                    _ => group.Length > 0 ? group : body,
                });
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }

    private static string Group(Match match, int number) => number < match.Groups.Count ? match.Groups[number].Value : string.Empty;

    private static string Conditional(string body, bool matched)
    {
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (matched ? body : string.Empty) : matched ? body[..colon] : body[(colon + 1)..];
    }

    /// <summary>"my-file_name" → "MyFileName" (or "myFileName").</summary>
    private static string Case(string text, bool pascal)
    {
        var result = string.Concat(Regex.Split(text, "[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return pascal || result.Length == 0 ? result : char.ToLowerInvariant(result[0]) + result[1..];
    }

    private static int ReadNumber(string text, ref int i)
    {
        var number = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            number = number * 10 + (text[i++] - '0');
        }

        return number;
    }

    /// <summary>After the colon of <c>${1:...}</c>: the text up to the closing brace (consumed), escapes resolved.</summary>
    private static string ReadBraced(string text, ref int i)
    {
        i++; // :
        var body = new StringBuilder();
        while (i < text.Length && text[i] != '}')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            body.Append(text[i++]);
        }

        i++; // }
        return body.ToString();
    }
}
