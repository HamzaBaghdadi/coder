using System.Text;

namespace Coder.Core.Emmet;

/// <summary>
/// Parses an abbreviation (<c>ul>li.item$*3>a[href=#]{Link $}</c>) into a tree: names, <c>.class</c>,
/// <c>#id</c>, <c>[attributes]</c>, <c>{text}</c>, <c>/</c>, the operators <c>&gt;</c> <c>+</c> <c>^</c>,
/// <c>(groups)</c> and <c>*N</c>. Returns null for anything malformed, so a half-typed abbreviation
/// (<c>ul&gt;</c>, <c>.</c>, <c>a[</c>) is simply not offered.
/// </summary>
internal sealed class EmmetParser
{
    private const int MaxRepeat = 500;

    private readonly string _source;
    private readonly Func<string, bool> _isSnippet;
    private int _index;

    private EmmetParser(string source, Func<string, bool> isSnippet)
    {
        _source = source;
        _isSnippet = isSnippet;
    }

    /// <summary>The abbreviation as a root group, or null when it does not parse.</summary>
    public static EmmetNode? Parse(string abbreviation, Func<string, bool>? isSnippet = null)
    {
        if (string.IsNullOrWhiteSpace(abbreviation))
        {
            return null;
        }

        var parser = new EmmetParser(abbreviation, isSnippet ?? (_ => false));
        try
        {
            var root = parser.ParseSequence();
            return parser._index == abbreviation.Length ? root : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private char Peek => _index < _source.Length ? _source[_index] : '\0';

    private bool AtEnd => _index >= _source.Length;

    private EmmetNode ParseSequence()
    {
        var root = new EmmetNode { IsGroup = true };
        var parents = new List<EmmetNode> { root };
        while (true)
        {
            var item = ParseItem();
            parents[^1].Children.Add(item);
            if (AtEnd || Peek == ')')
            {
                return root;
            }

            switch (Peek)
            {
                case '>':
                    _index++;
                    parents.Add(item);
                    break;
                case '+':
                    _index++;
                    break;
                case '^':
                    while (Peek == '^')
                    {
                        _index++;
                        if (parents.Count > 1)
                        {
                            parents.RemoveAt(parents.Count - 1);
                        }
                    }

                    break;
                default:
                    throw new FormatException();
            }

            if (AtEnd || Peek == ')')
            {
                throw new FormatException(); // an operator needs something after it
            }
        }
    }

    private EmmetNode ParseItem()
    {
        if (Peek == '(')
        {
            _index++;
            var group = ParseSequence();
            Expect(')');
            group.IsGroup = true;
            ParseRepeat(group);
            return group;
        }

        var node = new EmmetNode();
        var name = ReadWhile(IsNameChar);
        if (name.Length > 0)
        {
            if (!(char.IsAsciiLetter(name[0]) || name[0] is '!' or '_'))
            {
                throw new FormatException(); // "3d", "-x"
            }

            // "ul+", "dl+", "table+" are snippets of their own, not "ul" followed by a sibling.
            if (Peek == '+' && (_index + 1 >= _source.Length || _source[_index + 1] is '+' or '>' or '^' or ')') && _isSnippet(name + "+"))
            {
                _index++;
                name += "+";
            }

            node.Name = name;
        }

        var hasContent = name.Length > 0;
        while (!AtEnd)
        {
            switch (Peek)
            {
                case '.':
                    _index++;
                    node.SetAttribute(new EmmetAttribute("class", RequireIdent()));
                    break;
                case '#':
                    _index++;
                    node.SetAttribute(new EmmetAttribute("id", RequireIdent()));
                    break;
                case '[':
                    ParseAttributes(node);
                    break;
                case '{':
                    node.Text = (node.Text ?? string.Empty) + ReadText();
                    break;
                case '/' when !node.SelfClosing:
                    _index++;
                    node.SelfClosing = true;
                    break;
                default:
                    goto done;
            }

            hasContent = true;
        }

    done:
        if (!hasContent)
        {
            throw new FormatException();
        }

        ParseRepeat(node);
        return node;
    }

    private void ParseRepeat(EmmetNode node)
    {
        if (Peek != '*')
        {
            return;
        }

        _index++;
        var digits = ReadWhile(char.IsAsciiDigit);
        node.Repeat = digits.Length == 0 ? 1 : Math.Clamp(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), 1, MaxRepeat);
    }

    /// <summary><c>[a b=1 c="two words" d='x' e={expr} f.]</c></summary>
    private void ParseAttributes(EmmetNode node)
    {
        Expect('[');
        while (true)
        {
            SkipSpaces();
            if (AtEnd)
            {
                throw new FormatException();
            }

            if (Peek == ']')
            {
                _index++;
                return;
            }

            var name = ReadWhile(c => !char.IsWhiteSpace(c) && c is not '=' and not ']' and not '"' and not '\'');
            if (name.Length == 0)
            {
                throw new FormatException();
            }

            var boolean = name.Length > 1 && name[^1] == '.';
            if (boolean)
            {
                name = name[..^1];
            }

            string? value = null;
            if (Peek == '=')
            {
                _index++;
                value = Peek switch
                {
                    '"' or '\'' => ReadQuoted(),
                    '{' => "{" + ReadText() + "}",
                    _ => ReadUnquoted(),
                };
            }

            node.SetAttribute(new EmmetAttribute(name, value, boolean && value is null));
        }
    }

    private string ReadQuoted()
    {
        var quote = _source[_index++];
        var start = _index;
        while (!AtEnd && Peek != quote)
        {
            _index += Peek == '\\' ? 2 : 1;
        }

        if (AtEnd)
        {
            throw new FormatException();
        }

        var value = _source[start.._index];
        _index++;
        return value;
    }

    /// <summary>Up to a space or <c>]</c>, keeping <c>${1:a b}</c> fields whole.</summary>
    private string ReadUnquoted()
    {
        var start = _index;
        var depth = 0;
        while (!AtEnd && (depth > 0 || (!char.IsWhiteSpace(Peek) && Peek != ']')))
        {
            depth += Peek == '{' ? 1 : Peek == '}' ? -1 : 0;
            _index++;
        }

        return _source[start.._index];
    }

    /// <summary>The inside of <c>{…}</c>; nested braces (snippet fields) and <c>\}</c> escapes are kept.</summary>
    private string ReadText()
    {
        Expect('{');
        var text = new StringBuilder();
        var depth = 0;
        while (!AtEnd)
        {
            var c = _source[_index];
            if (c == '\\' && _index + 1 < _source.Length)
            {
                text.Append(c).Append(_source[_index + 1]);
                _index += 2;
                continue;
            }

            _index++;
            if (c == '}' && depth == 0)
            {
                return text.ToString();
            }

            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
            text.Append(c);
        }

        throw new FormatException();
    }

    private string RequireIdent()
    {
        var ident = ReadWhile(IsIdentChar);
        return ident.Length == 0 ? throw new FormatException() : ident;
    }

    private void Expect(char c)
    {
        if (Peek != c || AtEnd)
        {
            throw new FormatException();
        }

        _index++;
    }

    private void SkipSpaces()
    {
        while (!AtEnd && char.IsWhiteSpace(Peek))
        {
            _index++;
        }
    }

    private string ReadWhile(Func<char, bool> predicate)
    {
        var start = _index;
        while (!AtEnd && predicate(Peek))
        {
            _index++;
        }

        return _source[start.._index];
    }

    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '!' or '$' or '@';

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '$' or '@' or '!' or '%';
}
