namespace Coder.Core.Text;

/// <summary>
/// A selection between two document offsets. <see cref="Anchor"/> is where selecting started and stays
/// fixed while extending; <see cref="Active"/> is the caret. When both are equal it is a plain caret.
/// </summary>
public readonly record struct Selection(int Anchor, int Active)
{
    public static Selection Caret(int offset) => new(offset, offset);

    public int Start => Math.Min(Anchor, Active);

    public int End => Math.Max(Anchor, Active);

    public int Length => End - Start;

    public bool IsEmpty => Anchor == Active;

    public Selection CollapseTo(int offset) => Caret(offset);

    public Selection ExtendTo(int offset) => this with { Active = offset };

    public Selection Clamp(int documentLength) =>
        new(Math.Clamp(Anchor, 0, documentLength), Math.Clamp(Active, 0, documentLength));
}
