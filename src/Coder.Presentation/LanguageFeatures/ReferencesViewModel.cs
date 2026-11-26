using System.Collections.ObjectModel;
using Coder.Core.Lsp;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>A row of the References panel: a file with its count, or one reference in it.</summary>
public abstract record ReferenceRow(string Path);

public sealed record ReferenceFileRow(string Path, string Folder, int Count) : ReferenceRow(Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>One use of the symbol: its line, trimmed, with where the symbol is in that text (for highlighting).</summary>
public sealed record ReferenceItemRow(string Path, TextRange Range, string Preview, int MatchStart, int MatchLength) : ReferenceRow(Path)
{
    public int LineNumber => Range.Start.Line + 1;
}

/// <summary>
/// Find All References (Shift+F12) in the bottom panel: every use of the symbol, grouped by file, each
/// with its line as a preview and the symbol highlighted. F4 and Shift+F4 step through them.
/// </summary>
public sealed class ReferencesViewModel : ObservableObject
{
    private readonly LanguageServices _languages;
    private readonly EditorGroupViewModel _editors;
    private readonly Func<string?> _workspaceFolder;
    private readonly Func<string, TextRange, Task> _reveal;
    private string _title = "Use Find All References (Shift+F12) on a symbol to list its uses here.";
    private ReferenceItemRow? _current;
    private bool _isSearching;

    public ReferencesViewModel(LanguageServices languages, EditorGroupViewModel editors, Func<string?> workspaceFolder, Func<string, TextRange, Task> reveal)
    {
        _languages = languages;
        _editors = editors;
        _workspaceFolder = workspaceFolder;
        _reveal = reveal;
    }

    public ObservableCollection<ReferenceRow> Rows { get; } = [];

    /// <summary>"12 references to "Parse" in 4 files", or why there are none.</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    /// <summary>The reference shown last (F4 continues from it).</summary>
    public ReferenceItemRow? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public int Count => Rows.OfType<ReferenceItemRow>().Count();

    /// <summary>Asks the server for the uses of the symbol at <paramref name="offset"/>. Returns false when it couldn't.</summary>
    public async Task<bool> FindAsync(EditorTabViewModel tab, int offset)
    {
        if (!_languages.Supports(tab, c => c.HasReferences))
        {
            return false;
        }

        var (start, end) = tab.Editor.WordRangeAt(offset);
        var symbol = tab.Document.GetText(start, end - start).Trim();
        IsSearching = true;
        Title = "Finding references…";
        try
        {
            var locations = await _languages.FindReferencesAsync(tab, offset, CancellationToken.None) ?? [];
            Show(symbol, locations);
            return true;
        }
        finally
        {
            IsSearching = false;
        }
    }

    public Task ActivateAsync(ReferenceRow row)
    {
        if (row is not ReferenceItemRow item)
        {
            return Task.CompletedTask;
        }

        Current = item;
        return _reveal(item.Path, item.Range);
    }

    /// <summary>F4 / Shift+F4: the next or previous reference, wrapping around.</summary>
    public Task StepAsync(int direction)
    {
        var items = Rows.OfType<ReferenceItemRow>().ToList();
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var index = Current is null ? (direction > 0 ? -1 : 0) : items.IndexOf(Current);
        return ActivateAsync(items[((index + direction) % items.Count + items.Count) % items.Count]);
    }

    public void Clear()
    {
        Rows.Clear();
        Current = null;
        Title = "Use Find All References (Shift+F12) on a symbol to list its uses here.";
        OnPropertyChanged(nameof(Count));
    }

    private void Show(string symbol, IReadOnlyList<Location> locations)
    {
        Rows.Clear();
        Current = null;
        var root = _workspaceFolder();
        var lines = new Dictionary<string, IReadOnlyList<string>>(DocumentUri.PathComparer);
        var files = locations.GroupBy(l => l.Path, DocumentUri.PathComparer).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in files)
        {
            var folder = Path.GetDirectoryName(file.Key) ?? string.Empty;
            if (root is not null && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetRelativePath(root, folder) is "." ? string.Empty : Path.GetRelativePath(root, folder);
            }

            var uses = file.OrderBy(l => l.Range.Start).ToList();
            Rows.Add(new ReferenceFileRow(file.Key, folder.Replace('\\', '/'), uses.Count));
            foreach (var use in uses)
            {
                Rows.Add(Preview(use, LinesOf(file.Key, lines)));
            }
        }

        // Name the symbol as the server found it (the caret may sit just after it, on punctuation).
        if (Rows.OfType<ReferenceItemRow>().FirstOrDefault(r => r.MatchLength > 0) is { } found)
        {
            symbol = found.Preview.Substring(found.MatchStart, found.MatchLength);
        }

        var count = locations.Count;
        Title = count == 0
            ? $"No references to \"{symbol}\" found."
            : $"{count} {(count == 1 ? "reference" : "references")} to \"{symbol}\" in {files.Count} {(files.Count == 1 ? "file" : "files")}";
        OnPropertyChanged(nameof(Count));
    }

    /// <summary>The reference's line without its indentation, and where the symbol sits in it.</summary>
    private static ReferenceItemRow Preview(Location location, IReadOnlyList<string> lines)
    {
        var (start, end) = (location.Range.Start, location.Range.End);
        var line = start.Line < lines.Count ? lines[start.Line] : string.Empty;
        var indent = line.Length - line.TrimStart().Length;
        var text = line.Trim();
        var matchStart = Math.Clamp(start.Column - indent, 0, text.Length);
        var matchEnd = end.Line == start.Line ? Math.Clamp(end.Column - indent, matchStart, text.Length) : text.Length;
        return new ReferenceItemRow(location.Path, location.Range, text, matchStart, matchEnd - matchStart);
    }

    /// <summary>A file's lines: from its open editor (unsaved edits included), else read once from disk.</summary>
    private IReadOnlyList<string> LinesOf(string path, Dictionary<string, IReadOnlyList<string>> cache)
    {
        if (!cache.TryGetValue(path, out var lines))
        {
            lines = _editors.FindTab(path) is { } open
                ? Enumerable.Range(0, open.Document.LineCount).Select(open.Document.GetLineText).ToList()
                : File.Exists(path) ? File.ReadAllLines(path) : [];
            cache[path] = lines;
        }

        return lines;
    }
}
