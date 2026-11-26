namespace Coder.Core.Syntax;

public enum TokenKind : byte
{
    Text,
    Keyword,
    Type,
    Function,
    String,
    Number,
    Comment,
    Constant,
    Operator,
    Punctuation,
    Preprocessor,
}

/// <summary>A colored span inside a single line. <see cref="Start"/> is a column in that line.</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>What a line leaves open for the next one, e.g. an unterminated block comment.</summary>
public enum LexState : byte
{
    Normal,
    BlockComment,
    MultiLineString,
}
