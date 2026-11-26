using System.Collections.ObjectModel;
using System.Globalization;
using Coder.Core.Search;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Explorer;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Services;

namespace Coder.Presentation.Search;

/// <summary>
/// "Search in files" side bar. Typing waits briefly before searching, a newer query cancels the older
/// search, and results stream in file by file so the first hits show up immediately.
/// </summary>
public sealed class SearchViewModel : ObservableObject, ISearchOptions, IDisposable
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(250);

    private readonly WorkspaceSearch _search;
    private readonly ExplorerViewModel _explorer;
    private readonly EditorGroupViewModel _editors;
    private readonly Func<string, Task> _openFile;
    private readonly ReplaceInFiles _replacer;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _running;

    private string _query = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private bool _isSearching;
    private int _matchCount;
    private int _fileCount;
    private string? _error;
    private string? _notice;
    private SearchRowViewModel? _selectedRow;
    private string _replacement = string.Empty;
    private bool _isReplaceVisible;
    private bool _isReplacing;

    /// <param name="openFile">Opens a file and reports failures (owned by the workbench).</param>
    public SearchViewModel(
        WorkspaceSearch search,
        ReplaceInFiles replacer,
        ExplorerViewModel explorer,
        EditorGroupViewModel editors,
        IDialogService dialogs,
        Func<string, Task> openFile)
    {
        _search = search;
        _replacer = replacer;
        _explorer = explorer;
        _editors = editors;
        _dialogs = dialogs;
        _openFile = openFile;
        explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.RootPath))
            {
                Restart(debounce: false);
            }
        };
    }

    /// <summary>Asks the view to focus the query field.</summary>
    public event EventHandler? FocusRequested;

    public ObservableCollection<SearchRowViewModel> Rows { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Restart(debounce: true);
            }
        }
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetOption(ref _matchCase, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => SetOption(ref _wholeWord, value);
    }

    public bool UseRegex
    {
        get => _useRegex;
        set => SetOption(ref _useRegex, value);
    }

    public SearchRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool HasError => _error is not null;

    public string Replacement
    {
        get => _replacement;
        set
        {
            if (SetProperty(ref _replacement, value))
            {
                OnPropertyChanged(nameof(ReplacementPreview));
            }
        }
    }

    public bool IsReplaceVisible
    {
        get => _isReplaceVisible;
        set
        {
            if (SetProperty(ref _isReplaceVisible, value))
            {
                OnPropertyChanged(nameof(ReplacementPreview));
            }
        }
    }

    /// <summary>The replacement as shown next to each struck-through match (regex groups are not expanded).</summary>
    public string ReplacementPreview => IsReplaceVisible ? Replacement : string.Empty;

    public bool CanReplaceAll => _matchCount > 0 && !IsSearching && !_isReplacing && _error is null;

    /// <summary>"12 results in 3 files", "Searching…", "No results found", an error, or a hint.</summary>
    public string Summary
    {
        get
        {
            if (_error is not null)
            {
                return _error;
            }

            if (_notice is not null)
            {
                return _notice;
            }

            if (!_explorer.HasFolder)
            {
                return "Open a folder to search its files.";
            }

            if (Query.Length == 0)
            {
                return string.Empty;
            }

            if (IsSearching && _matchCount == 0)
            {
                return "Searching…";
            }

            if (_matchCount == 0)
            {
                return "No results found.";
            }

            var limited = _matchCount >= WorkspaceSearch.MaxTotalMatches ? " (limit reached)" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture,
                $"{_matchCount:N0} {(_matchCount == 1 ? "result" : "results")} in {_fileCount:N0} {(_fileCount == 1 ? "file" : "files")}{limited}");
        }
    }

    /// <summary>Ctrl+Shift+F: seeds the query from a single-line editor selection, then focuses the field.</summary>
    /// <summary>"Clear Search Results": empties both the search and the replace text, like VS Code.</summary>
    public void Clear()
    {
        Replacement = string.Empty;
        Query = string.Empty;
    }

    public void Focus()
    {
        if (_editors.ActiveEditor?.Editor is { Selection: { IsEmpty: false, Length: < 200 } } editor
            && !editor.SelectedText.Contains('\n', StringComparison.Ordinal))
        {
            Query = editor.SelectedText;
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces every result after the user confirms. Files are searched again at replace time, so the
    /// counts reflect their current contents. Results refresh afterwards.
    /// </summary>
    public async Task ReplaceAllAsync()
    {
        if (!CanReplaceAll)
        {
            return;
        }

        var files = Rows.OfType<SearchFileRowViewModel>().Select(row => row.Result.Path).ToList();
        var replacementText = Replacement.Length == 0 ? "nothing (delete)" : $"“{Replacement}”";
        var confirmed = await _dialogs.ConfirmAsync(
            string.Create(CultureInfo.InvariantCulture, $"Replace {_matchCount:N0} {(_matchCount == 1 ? "occurrence" : "occurrences")} across {files.Count:N0} {(files.Count == 1 ? "file" : "files")} with {replacementText}?"),
            "Files open in an editor are changed in the editor (undo works, nothing is saved). Other files are changed on disk, which can't be undone.",
            "Replace All");
        if (!confirmed)
        {
            return;
        }

        _isReplacing = true;
        OnPropertyChanged(nameof(CanReplaceAll));
        var outcome = await _replacer.ReplaceAsync(files, Query, Replacement, new SearchOptions(MatchCase, WholeWord, UseRegex));
        _isReplacing = false;

        Restart(debounce: false);
        _notice = Describe(outcome);
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Enter / click: toggles a file's matches, or opens a match at its exact location.</summary>
    public async Task ActivateAsync(SearchRowViewModel row)
    {
        switch (row)
        {
            case SearchFileRowViewModel file:
                SetExpanded(file, !file.IsExpanded);
                break;
            case SearchMatchRowViewModel match:
                var path = match.File.Result.Path;
                await _openFile(path);
                if (_editors.ActiveEditor is { } tab && string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    var start = tab.Document.GetOffset(match.Match.Position);
                    tab.Editor.SetSelection(new Selection(start, Math.Min(start + match.Match.Length, tab.Document.Length)));
                }

                break;
        }
    }

    public void SetExpanded(SearchFileRowViewModel file, bool expanded)
    {
        if (file.IsExpanded == expanded)
        {
            return;
        }

        file.IsExpanded = expanded;
        var index = Rows.IndexOf(file) + 1;
        if (expanded)
        {
            foreach (var row in file.MatchRows)
            {
                Rows.Insert(index++, row);
            }
        }
        else
        {
            foreach (var row in file.MatchRows)
            {
                Rows.Remove(row);
            }
        }
    }

    private void SetOption(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            Restart(debounce: false);
        }
    }

    /// <summary>Stops a search that is still running.</summary>
    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }

    /// <summary>Cancels any running search, clears results and starts over.</summary>
    private void Restart(bool debounce)
    {
        Dispose();

        Rows.Clear();
        SelectedRow = null;
        _matchCount = 0;
        _fileCount = 0;
        _error = null;
        _notice = null;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanReplaceAll));

        if (Query.Length == 0 || _explorer.RootPath is not { } root)
        {
            IsSearching = false;
            OnPropertyChanged(nameof(Summary));
            return;
        }

        var options = new SearchOptions(MatchCase, WholeWord, UseRegex);
        if (UseRegex && TextSearch.FindAll(string.Empty, Query, options).Error is { } error)
        {
            _error = error;
            IsSearching = false;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(Summary));
            return;
        }

        _running = new CancellationTokenSource();
        _ = RunAsync(root, Query, options, debounce, _running.Token);
    }

    private async Task RunAsync(string root, string query, SearchOptions options, bool debounce, CancellationToken token)
    {
        IsSearching = true;
        OnPropertyChanged(nameof(Summary));

        // Progress<T> posts back to the UI thread; results of a superseded search are dropped.
        var progress = new Progress<FileSearchResult>(result =>
        {
            if (!token.IsCancellationRequested)
            {
                AddResult(result, root);
            }
        });

        try
        {
            if (debounce)
            {
                await Task.Delay(TypingDelay, token);
            }

            await _search.SearchAsync(root, query, options, progress, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _error = exception.Message;
            OnPropertyChanged(nameof(HasError));
        }

        IsSearching = false;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanReplaceAll));
    }

    private static string Describe(ReplaceOutcome outcome)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Replaced {outcome.Replacements:N0} {(outcome.Replacements == 1 ? "occurrence" : "occurrences")} in {outcome.Files:N0} {(outcome.Files == 1 ? "file" : "files")}.");
        if (outcome.FailedFiles.Count > 0)
        {
            text += $" Couldn't change: {string.Join(", ", outcome.FailedFiles.Select(f => Path.GetFileName(f.Path)))}.";
        }

        return text;
    }

    private void AddResult(FileSearchResult result, string root)
    {
        var file = new SearchFileRowViewModel(result, root);
        file.MatchRows.AddRange(result.Matches.Select(match => new SearchMatchRowViewModel(file, match)));

        Rows.Add(file);
        foreach (var row in file.MatchRows)
        {
            Rows.Add(row);
        }

        _fileCount++;
        _matchCount += result.Matches.Count;
        OnPropertyChanged(nameof(Summary));
    }
}
