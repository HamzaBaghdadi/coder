namespace Coder.Core.Editing;

public sealed record EditorOptions
{
    public int TabSize { get; init; } = 4;

    /// <summary>Insert spaces when Tab is pressed instead of a tab character.</summary>
    public bool InsertSpaces { get; init; } = true;

    /// <summary>Typing an opening bracket or quote also inserts its closing partner.</summary>
    public bool AutoClosePairs { get; init; } = true;

    public string IndentUnit => InsertSpaces ? new string(' ', TabSize) : "\t";
}
