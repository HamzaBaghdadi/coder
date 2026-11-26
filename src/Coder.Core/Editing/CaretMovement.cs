namespace Coder.Core.Editing;

public enum CaretMovement
{
    Left,
    Right,
    Up,
    Down,
    WordLeft,
    WordRight,

    /// <summary>Toggles between the first non-blank character and column 0 ("smart home").</summary>
    LineStart,
    LineEnd,
    DocumentStart,
    DocumentEnd,
}
