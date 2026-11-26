namespace Coder.Core.Text;

/// <summary>
/// A single replace operation: <see cref="RemovedText"/> at <see cref="Offset"/> became <see cref="InsertedText"/>.
/// <see cref="StartLine"/> and <see cref="RemovedLineBreaks"/> let listeners (renderers, highlighters)
/// invalidate only what actually changed.
/// </summary>
public sealed record TextChange(int Offset, string RemovedText, string InsertedText, int StartLine)
{
    public int RemovedLineBreaks { get; } = CountLineBreaks(RemovedText);

    public int InsertedLineBreaks { get; } = CountLineBreaks(InsertedText);

    /// <summary>True when the change added or removed lines (rather than editing inside one line).</summary>
    public bool ChangesLineCount => RemovedLineBreaks != InsertedLineBreaks;

    /// <summary>The change that undoes this one.</summary>
    public TextChange Invert() => new(Offset, InsertedText, RemovedText, StartLine);

    /// <summary>Where an offset in the text before this change ends up after it. Offsets inside the removed text move to the end of the inserted text.</summary>
    public int MapOffset(int offset)
    {
        var removedEnd = Offset + RemovedText.Length;
        if (offset <= Offset)
        {
            return offset;
        }

        return offset >= removedEnd
            ? offset + InsertedText.Length - RemovedText.Length
            : Offset + InsertedText.Length;
    }

    private static int CountLineBreaks(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
