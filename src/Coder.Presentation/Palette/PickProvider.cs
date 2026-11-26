namespace Coder.Presentation.Palette;

/// <summary>
/// A one-off palette list for <see cref="CommandPaletteViewModel.Pick"/>: rows computed from the typed
/// query. <see cref="FromList"/> covers the common case of a fixed list filtered by fuzzy match.
/// </summary>
public sealed class PickProvider(string placeholder, Func<string, IEnumerable<PaletteItem>> items) : IPaletteProvider
{
    public string Prefix => string.Empty;

    public string Placeholder => placeholder;

    /// <summary>
    /// Called with the highlighted row as the selection moves (like VS Code's theme picker, which shows
    /// each theme as you go); null when nothing is highlighted.
    /// </summary>
    public Action<PaletteItem?>? Preview { get; init; }

    /// <summary>Called when the palette closes without a choice, to undo the previews.</summary>
    public Action? Cancelled { get; init; }

    public IEnumerable<PaletteItem> GetItems(string query) => items(query);

    public static PickProvider FromList(string placeholder, IReadOnlyList<PaletteItem> items) =>
        new(placeholder, query => CommandPaletteViewModel.Rank(items, query));
}
