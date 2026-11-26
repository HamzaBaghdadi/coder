namespace Coder.Core.Editing;

/// <summary>Character classes used to find word boundaries (Ctrl+Arrow, double-click, Ctrl+Backspace).</summary>
internal enum CharClass
{
    Whitespace,
    Word,
    Punctuation,
}

internal static class CharClassifier
{
    public static CharClass Classify(char c) =>
        char.IsWhiteSpace(c) ? CharClass.Whitespace
        : char.IsLetterOrDigit(c) || c == '_' ? CharClass.Word
        : CharClass.Punctuation;

    public static bool IsWordChar(char c) => Classify(c) == CharClass.Word;
}
