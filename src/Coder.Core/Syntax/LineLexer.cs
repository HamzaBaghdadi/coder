using System.Collections.Frozen;

namespace Coder.Core.Syntax;

/// <summary>
/// A fast, allocation-free, single-line lexer driven by a <see cref="LanguageDefinition"/>.
/// It is deliberately approximate (no parsing): good coloring at a tiny, predictable cost per line.
/// State that crosses lines (block comments, multi-line strings) flows through <see cref="LexState"/>.
/// </summary>
public sealed class LineLexer
{
    private const string Operators = "+-*/%=<>!&|^~?:";
    private const string Punctuation = "()[]{};,.";

    private readonly LanguageDefinition _language;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywords;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _types;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _constants;

    public LineLexer(LanguageDefinition language)
    {
        _language = language;
        _keywords = language.Keywords.GetAlternateLookup<ReadOnlySpan<char>>();
        _types = language.Types.GetAlternateLookup<ReadOnlySpan<char>>();
        _constants = language.Constants.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Appends the tokens of <paramref name="line"/> and returns the state for the next line.</summary>
    public LexState Tokenize(ReadOnlySpan<char> line, LexState state, List<Token> tokens)
    {
        var i = 0;
        switch (state)
        {
            case LexState.BlockComment:
                if (!TryClose(line, ref i, _language.BlockComment!.Value.Close, TokenKind.Comment, tokens))
                {
                    return LexState.BlockComment;
                }

                break;
            case LexState.MultiLineString:
                if (!TryClose(line, ref i, _language.MultiLineStringDelimiter!, TokenKind.String, tokens))
                {
                    return LexState.MultiLineString;
                }

                break;
        }

        if (i == 0 && _language.HashLinesAreDirectives && line.TrimStart().StartsWith('#'))
        {
            Add(tokens, 0, line.Length, TokenKind.Preprocessor);
            return LexState.Normal;
        }

        var insideTag = false;
        while (i < line.Length)
        {
            var c = line[i];
            var rest = line[i..];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            // Block comments are checked first: some open with the line-comment marker ("--[[" in Lua).
            else if (_language.BlockComment is { } block && rest.StartsWith(block.Open))
            {
                var start = i;
                i += block.Open.Length;
                if (!TryClose(line, ref i, block.Close, TokenKind.Comment, tokens, start))
                {
                    return LexState.BlockComment;
                }
            }
            else if (_language.LineComment is { } lineComment && rest.StartsWith(lineComment))
            {
                Add(tokens, i, line.Length - i, TokenKind.Comment);
                return LexState.Normal;
            }
            else if (_language.MultiLineStringDelimiter is { } delimiter && rest.StartsWith(delimiter))
            {
                var start = i;
                i += delimiter.Length;
                if (!TryClose(line, ref i, delimiter, TokenKind.String, tokens, start))
                {
                    return LexState.MultiLineString;
                }
            }
            else if (_language.StringQuotes.Contains(c) && (!_language.MarkupTags || insideTag))
            {
                var start = i;
                i = SkipString(line, i);
                Add(tokens, start, i - start, TokenKind.String);
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])))
            {
                var start = i;
                i = SkipNumber(line, i);
                Add(tokens, start, i - start, TokenKind.Number);
            }
            else if (IsIdentifierStart(c))
            {
                var start = i;
                i = SkipIdentifier(line, i);
                var kind = ClassifyIdentifier(line, start, i, insideTag);
                if (kind != TokenKind.Text)
                {
                    Add(tokens, start, i - start, kind);
                }
            }
            else
            {
                if (_language.MarkupTags)
                {
                    insideTag = c switch
                    {
                        '<' => true,
                        '>' => false,
                        _ => insideTag,
                    };
                }

                var kind = Operators.Contains(c) ? TokenKind.Operator
                    : Punctuation.Contains(c) ? TokenKind.Punctuation
                    : TokenKind.Text;
                if (kind != TokenKind.Text)
                {
                    Add(tokens, i, 1, kind);
                }

                i++;
            }
        }

        return LexState.Normal;
    }

    private TokenKind ClassifyIdentifier(ReadOnlySpan<char> line, int start, int end, bool insideTag)
    {
        var word = line[start..end];
        if (_language.MarkupTags)
        {
            if (!insideTag)
            {
                return TokenKind.Text;
            }

            var before = line[..start].TrimEnd();
            return before.EndsWith('<') || before.EndsWith("</") ? TokenKind.Keyword : TokenKind.Type;
        }

        if (_keywords.Contains(word))
        {
            return TokenKind.Keyword;
        }

        if (_types.Contains(word))
        {
            return TokenKind.Type;
        }

        if (_constants.Contains(word))
        {
            return TokenKind.Constant;
        }

        if (line[end..].TrimStart().StartsWith('('))
        {
            return TokenKind.Function;
        }

        if (_language.PascalCaseIsType && char.IsUpper(word[0]) && word.ContainsAnyInRange('a', 'z'))
        {
            return TokenKind.Type;
        }

        return TokenKind.Text;
    }

    /// <summary>
    /// Looks for <paramref name="close"/> from <paramref name="i"/>. Emits the span from
    /// <paramref name="tokenStart"/> either up to and including the delimiter (returns true) or to the
    /// end of the line (returns false, meaning the construct continues on the next line).
    /// </summary>
    private static bool TryClose(ReadOnlySpan<char> line, ref int i, string close, TokenKind kind, List<Token> tokens, int tokenStart = 0)
    {
        var found = line[i..].IndexOf(close);
        if (found < 0)
        {
            Add(tokens, tokenStart, line.Length - tokenStart, kind);
            i = line.Length;
            return false;
        }

        i += found + close.Length;
        Add(tokens, tokenStart, i - tokenStart, kind);
        return true;
    }

    private static int SkipString(ReadOnlySpan<char> line, int i)
    {
        var quote = line[i++];
        while (i < line.Length)
        {
            var c = line[i++];
            if (c == '\\')
            {
                i++;
            }
            else if (c == quote)
            {
                break;
            }
        }

        return Math.Min(i, line.Length);
    }

    private static int SkipNumber(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] is '_' or '.'))
        {
            // Stop before a member access such as "1.ToString()".
            if (line[i] == '.' && (i + 1 >= line.Length || !char.IsAsciiDigit(line[i + 1])))
            {
                break;
            }

            i++;
        }

        return i;
    }

    private bool IsIdentifierStart(char c) => char.IsLetter(c) || _language.ExtraIdentifierChars.Contains(c);

    private int SkipIdentifier(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || _language.ExtraIdentifierChars.Contains(line[i])))
        {
            i++;
        }

        return i;
    }

    private static void Add(List<Token> tokens, int start, int length, TokenKind kind)
    {
        if (length > 0)
        {
            tokens.Add(new Token(start, length, kind));
        }
    }
}
