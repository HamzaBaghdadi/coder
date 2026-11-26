namespace Coder.Presentation.Palette;

/// <summary>A row in the command palette.</summary>
/// <param name="Label">Main text.</param>
/// <param name="Detail">Secondary, dimmer text (folder path, category).</param>
/// <param name="Hint">Right-aligned text, typically a key binding.</param>
/// <param name="Accept">What happens when the row is chosen.</param>
public sealed record PaletteItem(string Label, string Detail, string? Hint, Func<Task> Accept)
{
    /// <summary>A file the row stands for: the palette shows its icon, as the explorer does.</summary>
    public string? FilePath { get; init; }
}
