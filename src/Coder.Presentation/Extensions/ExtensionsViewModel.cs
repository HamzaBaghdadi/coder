using System.Collections.ObjectModel;
using System.Globalization;
using Coder.Core.Extensions;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Extensions;

/// <summary>
/// The Extensions view (Ctrl+Shift+X): what is installed, and what the sources offer for the search
/// (popular ones before anything is typed). Install and uninstall from a row; an installed theme can
/// be applied at once.
/// </summary>
public sealed class ExtensionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private readonly ExtensionStore _store;
    private readonly IReadOnlyList<IExtensionSource> _sources;
    private readonly Func<string, Task> _pickTheme;
    private CancellationTokenSource? _search;
    private string _query = string.Empty;
    private bool _isSearching;
    private string _message = string.Empty;
    private bool _loaded;

    /// <param name="pickTheme">Shows the color themes whose id starts with the given prefix, to choose one.</param>
    public ExtensionsViewModel(ExtensionStore store, IReadOnlyList<IExtensionSource> sources, Func<string, Task> pickTheme)
    {
        _store = store;
        _sources = sources;
        _pickTheme = pickTheme;
        RefreshInstalled();
        _store.Changed += OnStoreChanged; // Settings Sync installs and uninstalls too
    }

    /// <summary>Installed extensions matching the search.</summary>
    public ObservableCollection<ExtensionItemViewModel> Installed { get; } = [];

    /// <summary>What the sources offer for the search (installed ones are listed above, not here).</summary>
    public ObservableCollection<ExtensionItemViewModel> Results { get; } = [];

    public bool HasSources => _sources.Count > 0;

    /// <summary>"Themes from Open VSX", or "Themes from Open VSX and Coder".</summary>
    public string SourcesLabel => $"Marketplace · {string.Join(" + ", _sources.Select(s => s.Name))}";

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                RefreshInstalled();
                _ = SearchAsync(delay: true);
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    /// <summary>Why there are no results, or what went wrong.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>The first time the view is shown: popular themes (nothing is fetched before that).</summary>
    public Task LoadAsync()
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }

        _loaded = true;
        return SearchAsync(delay: false);
    }

    public Task SearchAsync() => SearchAsync(delay: false);

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        var search = _search;
        _search = null;
        search?.Cancel();
        search?.Dispose();
    }

    private async Task SearchAsync(bool delay)
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        try
        {
            if (delay)
            {
                await Task.Delay(SearchDelay, search.Token); // typing: wait for a pause
            }

            IsSearching = true;
            Message = string.Empty;
            var found = new List<ExtensionListing>();
            var errors = new List<string>();
            foreach (var source in _sources)
            {
                try
                {
                    found.AddRange(await source.SearchAsync(_query, search.Token));
                }
                catch (ExtensionException exception)
                {
                    errors.Add(exception.Message);
                }
            }

            search.Token.ThrowIfCancellationRequested();
            var installed = _store.Installed().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            Results.Clear();
            foreach (var listing in Rank(found.DistinctBy(l => l.Id, StringComparer.OrdinalIgnoreCase).Where(l => !installed.ContainsKey(l.Id)), _query))
            {
                Results.Add(new ExtensionItemViewModel(this, listing, null));
            }

            Message = errors.Count > 0 ? string.Join(" ", errors)
                : Results.Count == 0 && Installed.Count == 0 ? (_query.Length > 0 ? $"No themes match \"{_query}\"." : "Nothing to show yet.")
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
        finally
        {
            if (ReferenceEquals(search, _search))
            {
                IsSearching = false;
            }
        }
    }

    internal async Task InstallAsync(ExtensionItemViewModel item)
    {
        if (item.Listing is not { } listing || item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        item.Status = "Installing…";
        try
        {
            var installed = await _store.InstallAsync(listing);
            Results.Remove(item);
            RefreshInstalled();
            Message = string.Empty;
            if (installed.Themes.Count > 0)
            {
                await _pickTheme(ThemePrefix(installed.Id)); // ready to try it right away
            }
        }
        catch (ExtensionException exception)
        {
            item.Status = string.Empty;
            Message = exception.Message;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    internal async Task UninstallAsync(ExtensionItemViewModel item)
    {
        if (item.Installed is not { } installed)
        {
            return;
        }

        try
        {
            await _store.UninstallAsync(installed.Id); // stops its program first, if it runs one
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Message = $"{installed.DisplayName} couldn't be removed: {exception.Message}";
            return;
        }

        RefreshInstalled();
        _ = SearchAsync(delay: false); // it can be installed again from the results
    }

    internal Task SetThemeAsync(ExtensionItemViewModel item) =>
        item.Installed is { } installed ? _pickTheme(ThemePrefix(installed.Id)) : Task.CompletedTask;

    /// <summary>The theme catalog's ids for an extension's themes start with this.</summary>
    public static string ThemePrefix(string extensionId) => $"vscode:{extensionId}/";

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        RefreshInstalled();
        var installed = _store.Installed().Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var result in Results.Where(r => r.Listing is { } l && installed.Contains(l.Id)).ToList())
        {
            Results.Remove(result);
        }
    }

    private void RefreshInstalled()
    {
        Installed.Clear();
        foreach (var extension in _store.Installed().Where(e => Matches(e, _query)))
        {
            Installed.Add(new ExtensionItemViewModel(this, null, extension));
        }
    }

    /// <summary>
    /// Search results in order of relevance. Marketplaces search loosely (Open VSX answers "dracula" with
    /// any popular theme), so a result is kept only when every word of the query is in its name, id,
    /// publisher or description; names that match come before descriptions that do, then by downloads.
    /// With no query, the popular list comes as the sources sent it.
    /// </summary>
    public static IEnumerable<ExtensionListing> Rank(IEnumerable<ExtensionListing> listings, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return listings;
        }

        var phrase = string.Join(' ', words);
        return listings
            .Select(l => (Listing: l, Score: Relevance(l, words, phrase)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Listing.DownloadCount)
            .Select(r => r.Listing);
    }

    private static int Relevance(ExtensionListing listing, string[] words, string phrase)
    {
        static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        var name = listing.DisplayName;
        var id = listing.Id;
        if (string.Equals(name, phrase, StringComparison.OrdinalIgnoreCase) || string.Equals(id, phrase, StringComparison.OrdinalIgnoreCase))
        {
            return 5; // exactly what was typed
        }

        if (name.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (words.All(w => Has(name, w)))
        {
            return 3;
        }

        if (words.All(w => Has(name, w) || Has(id, w) || Has(listing.Publisher, w)))
        {
            return 2;
        }

        return words.All(w => Has(name, w) || Has(id, w) || Has(listing.Publisher, w) || Has(listing.Description, w)) ? 1 : 0;
    }

    private static bool Matches(InstalledExtension extension, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            extension.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase) || extension.Id.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One row: an installed extension, or one a source offers.</summary>
public sealed class ExtensionItemViewModel : ObservableObject
{
    private readonly ExtensionsViewModel _owner;
    private bool _isBusy;
    private string _status = string.Empty;

    public ExtensionItemViewModel(ExtensionsViewModel owner, ExtensionListing? listing, InstalledExtension? installed)
    {
        _owner = owner;
        Listing = listing;
        Installed = installed;
        Install = new RelayCommand(() => _ = _owner.InstallAsync(this));
        Uninstall = new RelayCommand(() => _ = _owner.UninstallAsync(this));
        SetTheme = new RelayCommand(() => _ = _owner.SetThemeAsync(this));
    }

    public ExtensionListing? Listing { get; }

    public InstalledExtension? Installed { get; }

    public bool IsInstalled => Installed is not null;

    public bool HasThemes => Installed is { Themes.Count: > 0 };

    public string Id => Installed?.Id ?? Listing!.Id;

    public string DisplayName => Installed?.DisplayName ?? Listing!.DisplayName;

    public string Description => Installed?.Description ?? Listing!.Description;

    public string Version => Installed?.Version ?? Listing!.Version;

    public string? IconUrl => Listing?.IconUrl;

    public string? IconPath => Installed?.IconPath;

    /// <summary>"dracula-theme · 474K installs · Open VSX", "dracula-theme · 2 themes" or "coder · snippets for csharp, razor".</summary>
    public string Details => Installed is { } installed
        ? $"{installed.Publisher} · {Contents(installed)}"
        : $"{Listing!.Publisher}{(Listing.Verified ? " ✓" : string.Empty)} · {Count(Listing.DownloadCount)} installs · {Listing.Source}{(Listing.RunsProgram ? " · runs a program" : string.Empty)}";

    private static string Contents(InstalledExtension installed)
    {
        var parts = new List<string>();
        if (installed.Themes.Count > 0 || (installed.SnippetLanguages.Count == 0 && !installed.DeclaresProgram))
        {
            parts.Add(installed.Themes.Count == 1 ? "1 theme" : $"{installed.Themes.Count} themes");
        }

        if (installed.SnippetLanguages.Count > 0)
        {
            var languages = installed.SnippetLanguages.Where(l => l != "*").ToList();
            parts.Add(languages.Count == 0 ? "snippets" : $"snippets for {string.Join(", ", languages)}");
        }

        if (installed.DeclaresProgram)
        {
            parts.Add(installed.ProgramPath is null ? "runs a program (not built for this computer)" : "runs a program");
        }

        return string.Join(" · ", parts);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public System.Windows.Input.ICommand Install { get; }

    public System.Windows.Input.ICommand Uninstall { get; }

    public System.Windows.Input.ICommand SetTheme { get; }

    private static string Count(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}
