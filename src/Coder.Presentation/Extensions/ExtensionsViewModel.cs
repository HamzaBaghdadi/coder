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
    }

    /// <summary>Installed extensions matching the search.</summary>
    public ObservableCollection<ExtensionItemViewModel> Installed { get; } = [];

    /// <summary>What the sources offer for the search (installed ones are listed above, not here).</summary>
    public ObservableCollection<ExtensionItemViewModel> Results { get; } = [];

    public bool HasSources => _sources.Count > 0;

    /// <summary>"Themes from Open VSX", or "Themes from Open VSX and Coder".</summary>
    public string SourcesLabel => $"MARKETPLACE · {string.Join(" + ", _sources.Select(s => s.Name.ToUpperInvariant()))}";

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
            foreach (var listing in found.DistinctBy(l => l.Id, StringComparer.OrdinalIgnoreCase).Where(l => !installed.ContainsKey(l.Id)))
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

    internal void Uninstall(ExtensionItemViewModel item)
    {
        if (item.Installed is not { } installed)
        {
            return;
        }

        try
        {
            _store.Uninstall(installed.Id);
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

    private void RefreshInstalled()
    {
        Installed.Clear();
        foreach (var extension in _store.Installed().Where(e => Matches(e, _query)))
        {
            Installed.Add(new ExtensionItemViewModel(this, null, extension));
        }
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
        Uninstall = new RelayCommand(() => _owner.Uninstall(this));
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

    /// <summary>"dracula-theme · 474K installs · Open VSX", or "dracula-theme · 2 themes".</summary>
    public string Details => Installed is { } installed
        ? $"{installed.Publisher} · {(installed.Themes.Count == 1 ? "1 theme" : $"{installed.Themes.Count} themes")}"
        : $"{Listing!.Publisher}{(Listing.Verified ? " ✓" : string.Empty)} · {Count(Listing.DownloadCount)} installs · {Listing.Source}";

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
