using Coder.Core.Lsp;

namespace Coder.Presentation.Editors;

/// <summary>How a line decoration is colored: like a diagnostic of that severity, or muted.</summary>
public enum DecorationKind
{
    Default,
    Hint,
    Information,
    Warning,
    Error,
}

/// <summary>Text shown after the end of a line (an extension's annotation, such as Error Lens's message).</summary>
/// <param name="Line">Zero-based.</param>
public sealed record LineDecoration(int Line, string Text, DecorationKind Kind);

/// <summary>
/// End-of-line decorations by file, each owner (an extension) with its own set: setting a file's decorations
/// replaces only that owner's. When owners decorate the same line, the most severe is shown.
/// </summary>
public sealed class LineDecorations
{
    /// <summary>Longest text kept for one decoration.</summary>
    public const int MaxTextLength = 500;

    private readonly Dictionary<string, Dictionary<string, Dictionary<int, LineDecoration>>> _byFile = new(DocumentUri.PathComparer);

    /// <summary>A file's decorations changed (its path).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Replaces <paramref name="owner"/>'s decorations in <paramref name="path"/> (none: clears them).</summary>
    public void Set(string owner, string path, IEnumerable<LineDecoration> decorations)
    {
        var lines = new Dictionary<int, LineDecoration>();
        foreach (var decoration in decorations.Where(d => d.Line >= 0 && d.Text.Length > 0))
        {
            var text = decoration.Text.ReplaceLineEndings(" ");
            lines[decoration.Line] = decoration with { Text = text.Length > MaxTextLength ? text[..MaxTextLength] + "…" : text };
        }

        if (!_byFile.TryGetValue(path, out var owners))
        {
            if (lines.Count == 0)
            {
                return;
            }

            _byFile[path] = owners = new Dictionary<string, Dictionary<int, LineDecoration>>(StringComparer.OrdinalIgnoreCase);
        }

        if (lines.Count == 0)
        {
            owners.Remove(owner);
        }
        else
        {
            owners[owner] = lines;
        }

        Changed?.Invoke(this, path);
    }

    /// <summary>Removes all of <paramref name="owner"/>'s decorations (its program stopped).</summary>
    public void Clear(string owner)
    {
        foreach (var (path, owners) in _byFile.Where(f => f.Value.ContainsKey(owner)).ToList())
        {
            owners.Remove(owner);
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>The decoration to show on a line of a file, or null.</summary>
    public LineDecoration? For(string? path, int line) =>
        path is not null && _byFile.TryGetValue(path, out var owners)
            ? owners.Values.Select(lines => lines.GetValueOrDefault(line)).OfType<LineDecoration>().MaxBy(d => d.Kind)
            : null;
}
