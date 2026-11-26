using Coder.Core.Lsp;
using Coder.Presentation.Editors;
using Coder.Presentation.LanguageFeatures;

namespace Coder.Presentation.Palette;

/// <summary>A palette mode whose results arrive later (from a language server): it says when to show them.</summary>
public interface IAsyncPaletteProvider : IPaletteProvider
{
    /// <summary>New results are ready for the current query.</summary>
    event EventHandler? ItemsChanged;
}

/// <summary>
/// "@" (Ctrl+Shift+O) — the symbols of the active file from its language server: classes, their
/// methods and fields, in file order, each with the type or namespace it is in. Choosing one selects its name.
/// </summary>
public sealed class DocumentSymbolPaletteProvider(LanguageServices languages, EditorGroupViewModel editors, Func<string, TextRange, Task> reveal)
    : IAsyncPaletteProvider
{
    private EditorTabViewModel? _tab;
    private int _version = -1;
    private IReadOnlyList<PaletteItem>? _items;
    private bool _loading;

    public event EventHandler? ItemsChanged;

    public string Prefix => "@";

    public string Placeholder => editors.ActiveEditor is null
        ? "Open a file to go to its symbols"
        : _items is { Count: 0 } && !_loading ? "No symbols found in this file" : "Type to filter the file's symbols";

    public IEnumerable<PaletteItem> GetItems(string query)
    {
        if (editors.ActiveEditor is not { } tab)
        {
            return [];
        }

        if (!ReferenceEquals(tab, _tab) || tab.Document.Version != _version)
        {
            (_tab, _version, _items) = (tab, tab.Document.Version, null);
            _ = LoadAsync(tab);
        }

        if (_items is null)
        {
            return _loading ? [new PaletteItem("Looking for symbols…", string.Empty, null, () => Task.CompletedTask)] : [];
        }

        // Without a query, file order (an outline); with one, best match first.
        return query.Length == 0 ? _items : CommandPaletteViewModel.Rank(_items, query);
    }

    private async Task LoadAsync(EditorTabViewModel tab)
    {
        _loading = true;
        if (!languages.Supports(tab, c => c.HasDocumentSymbols))
        {
            _loading = false;
            _items = [new PaletteItem($"No language server for {tab.Language.DisplayName} files offers symbols", string.Empty, null, () => Task.CompletedTask)];
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var symbols = await languages.DocumentSymbolsAsync(tab, CancellationToken.None) ?? [];
        _loading = false;
        if (!ReferenceEquals(tab, _tab))
        {
            return;
        }

        var items = new List<PaletteItem>();
        void Add(DocumentSymbol symbol, string? container)
        {
            var range = symbol.SelectionRange;
            items.Add(new PaletteItem(
                symbol.Name,
                $"{SymbolKinds.Label(symbol.Kind)}{(container is null ? string.Empty : $" · {container}")}",
                $"Ln {range.Start.Line + 1}",
                () => reveal(tab.FilePath!, range)));
            foreach (var child in symbol.Children.OrderBy(c => c.Range.Start))
            {
                Add(child, symbol.Name);
            }
        }

        foreach (var symbol in symbols.OrderBy(s => s.Range.Start))
        {
            Add(symbol, null);
        }

        _items = items;
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// "#" (Ctrl+T) — symbols by name across the whole workspace, from every running language server.
/// The server is asked again as the query changes, a moment after typing stops.
/// </summary>
public sealed class WorkspaceSymbolPaletteProvider(LanguageServices languages, Func<string?> workspaceFolder, Func<string, TextRange, Task> reveal)
    : IAsyncPaletteProvider
{
    private const int MaxResults = 200;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    private string? _query;
    private IReadOnlyList<PaletteItem> _items = [];
    private int _generation;

    public event EventHandler? ItemsChanged;

    public string Prefix => "#";

    public string Placeholder => languages.HasWorkspaceSymbols
        ? "Type the name of a class, function or other symbol"
        : "Open a file of the project to start its language server, then search its symbols";

    public IEnumerable<PaletteItem> GetItems(string query)
    {
        if (query != _query)
        {
            _query = query;
            _ = SearchAsync(query);
        }

        return _items;
    }

    private async Task SearchAsync(string query)
    {
        // Only the latest query's answer is shown: an earlier one that arrives late is dropped.
        var generation = ++_generation;
        await Task.Delay(Delay);
        if (generation != _generation)
        {
            return;
        }

        var symbols = query.Length == 0 ? [] : await languages.WorkspaceSymbolsAsync(query, CancellationToken.None);
        if (generation == _generation)
        {
            var root = workspaceFolder();
            var items = symbols
                .Select(symbol => new PaletteItem(
                    symbol.Name,
                    $"{SymbolKinds.Label(symbol.Kind)}{(symbol.Container is { Length: > 0 } c ? $" · {c}" : string.Empty)}",
                    root is null ? Path.GetFileName(symbol.Location.Path) : Path.GetRelativePath(root, symbol.Location.Path).Replace('\\', '/'),
                    () => reveal(symbol.Location.Path, symbol.Location.Range)))
                .ToList();

            // The servers match fuzzily in their own ways; rank the merged list consistently.
            _items = CommandPaletteViewModel.Rank(items, query).Take(MaxResults).ToList();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Readable names for symbol kinds.</summary>
public static class SymbolKinds
{
    public static string Label(SymbolKind kind) => kind switch
    {
        SymbolKind.EnumMember => "enum member",
        SymbolKind.TypeParameter => "type parameter",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
