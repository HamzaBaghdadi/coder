using System.Collections.ObjectModel;
using Coder.Core.Search;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Palette;

/// <summary>A source of palette rows for one mode (commands, files, go to line).</summary>
public interface IPaletteProvider
{
    /// <summary>The query prefix that selects this provider (">" for commands, ":" for lines, "" for files).</summary>
    string Prefix { get; }

    string Placeholder { get; }

    IEnumerable<PaletteItem> GetItems(string query);
}

/// <summary>
/// The quick-input overlay (Ctrl+P / Ctrl+Shift+P / Ctrl+G). Like VS Code, the first character of the
/// query picks the mode, so the user can switch modes just by typing ">" or ":".
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 60;

    private readonly IReadOnlyList<IPaletteProvider> _providers;
    private bool _isOpen;
    private string _query = string.Empty;
    private PaletteItem? _selectedItem;
    private string _placeholder = string.Empty;
    private IPaletteProvider? _pick;
    private bool _accepting;
    private IPaletteProvider? _current;

    public CommandPaletteViewModel(IEnumerable<IPaletteProvider> providers)
    {
        // Longest prefix first so the empty-prefix provider is the fallback.
        _providers = providers.OrderByDescending(provider => provider.Prefix.Length).ToList();
        foreach (var provider in _providers.OfType<IAsyncPaletteProvider>())
        {
            // Results from a language server arrive after the query: show them if that mode is still up.
            provider.ItemsChanged += (sender, _) =>
            {
                if (IsOpen && ReferenceEquals(sender, _current))
                {
                    Refresh();
                }
            };
        }
    }

    /// <summary>Raised when the palette closes, so the view can return focus to the editor.</summary>
    public event EventHandler? Closed;

    public ObservableCollection<PaletteItem> Items { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Refresh();
            }
        }
    }

    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    public PaletteItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value) && _pick is PickProvider { Preview: { } preview })
            {
                preview(value);
            }
        }
    }

    /// <summary>Opens the palette pre-filled with a mode prefix.</summary>
    public void Open(string prefix)
    {
        _pick = null;
        _query = prefix;
        OnPropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    /// <summary>
    /// Shows a one-off list (a quick pick, such as branches to switch to) instead of a mode. The whole
    /// query goes to that provider, without prefixes, until the palette closes.
    /// </summary>
    public void Pick(IPaletteProvider provider, string initialQuery = "")
    {
        _pick = provider;
        _query = initialQuery;
        OnPropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        var cancelled = _accepting ? null : (_pick as PickProvider)?.Cancelled;
        IsOpen = false;
        _pick = null;
        cancelled?.Invoke();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[(index + delta + Items.Count) % Items.Count];
    }

    public async Task AcceptAsync(PaletteItem? item = null)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        _accepting = true; // closing now is a choice, not a cancel
        try
        {
            Close();
        }
        finally
        {
            _accepting = false;
        }

        await item.Accept();
    }

    private void Refresh()
    {
        var provider = _current = _pick ?? _providers.First(p => Query.StartsWith(p.Prefix, StringComparison.Ordinal));
        Placeholder = provider.Placeholder;

        Items.Clear();
        foreach (var item in provider.GetItems(Query[provider.Prefix.Length..].Trim()).Take(MaxResults))
        {
            Items.Add(item);
        }

        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>Ranks items by fuzzy score on their label (and detail as a weaker signal).</summary>
    public static IEnumerable<PaletteItem> Rank(IEnumerable<PaletteItem> items, string query)
    {
        if (query.Length == 0)
        {
            return items;
        }

        return items
            .Select(item => (Item: item, Score: FuzzyMatcher.Score(query, item.Label) ?? FuzzyMatcher.Score(query, item.Detail) / 4))
            .Where(match => match.Score is not null)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Item);
    }
}
