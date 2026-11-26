using System.Collections.ObjectModel;
using Coder.Core.Git;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.SourceControl;

/// <summary>A row of the history list.</summary>
public abstract record HistoryRow;

/// <summary>A commit, with how to draw its part of the graph.</summary>
/// <param name="IsHead">The commit checked out now.</param>
public sealed record HistoryCommitRow(GitCommit Commit, GraphRow Graph, bool IsHead, bool IsExpanded, string When) : HistoryRow
{
    /// <summary>The lanes that continue below the commit, drawn straight through its file rows.</summary>
    public IReadOnlyList<(int Lane, int Color)> LanesBelow { get; } =
        Graph.Segments.Where(s => !s.IsTop).Select(s => (s.To, s.Color)).Distinct().ToList();
}

/// <summary>A file an expanded commit changed; opening it shows that commit's change to it.</summary>
public sealed record HistoryFileRow(HistoryCommitRow Owner, GitChange Change) : HistoryRow;

/// <summary>The last row when older commits exist: loads the next page.</summary>
public sealed record HistoryMoreRow : HistoryRow;

/// <summary>
/// The commit history of the open repository, drawn as a graph under the changes in Source Control
/// (like VS Code's Source Control Graph): the current branch and its upstream, newest first. A commit
/// expands to the files it changed; a file opens as a diff of that commit's change.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private const int PageSize = 100;

    private readonly Func<GitRepository?> _repository;
    private readonly Func<GitStatus?> _status;
    private readonly EditorGroupViewModel _editors;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<GitChange>> _files = new(StringComparer.Ordinal);
    private List<GitCommit> _commits = [];
    private bool _isExpanded = true;
    private bool _hasMore;
    private string _message = string.Empty;
    private Task _loading = Task.CompletedTask;
    private bool _loadAgain;
    private int _count = PageSize;

    public HistoryViewModel(Func<GitRepository?> repository, Func<GitStatus?> status, EditorGroupViewModel editors)
    {
        _repository = repository;
        _status = status;
        _editors = editors;
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    /// <summary>Whether the section is open; a closed section loads nothing.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Shown instead of the list: no commits yet, or git's error.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>
    /// Reads the history again (after a commit, checkout, pull…). Rows are only rebuilt when the commits or
    /// their branch labels changed, so expanded commits and the scroll position survive ordinary refreshes.
    /// A call during a load is folded into one more load.
    /// </summary>
    public Task RefreshAsync()
    {
        if (!_loading.IsCompleted)
        {
            _loadAgain = true;
            return _loading;
        }

        _loading = LoadLoopAsync();
        return _loading;
    }

    private async Task LoadLoopAsync()
    {
        do
        {
            _loadAgain = false;
            await LoadAsync(_count);
        }
        while (_loadAgain);
    }

    /// <summary>Shows the next page of older commits.</summary>
    public Task LoadMoreAsync()
    {
        _count = _commits.Count + PageSize;
        return RefreshAsync();
    }

    private async Task LoadAsync(int count)
    {
        if (!IsExpanded)
        {
            return;
        }

        if (_repository() is not { } repository)
        {
            Clear(string.Empty);
            return;
        }

        IReadOnlyList<GitCommit> commits;
        try
        {
            commits = await repository.LogAsync(count + 1, upstream: _status()?.Upstream is { } upstream && !(_status()?.IsUpstreamGone ?? false) ? upstream : null);
        }
        catch (GitException exception)
        {
            Clear(exception.Message);
            return;
        }

        if (!ReferenceEquals(repository, _repository()))
        {
            return; // another folder was opened meanwhile
        }

        var hasMore = commits.Count > count;
        var shown = commits.Take(count).ToList();
        if (hasMore == _hasMore && shown.Count == _commits.Count && shown.SequenceEqual(_commits, SameCommit.Instance))
        {
            return; // nothing changed
        }

        _commits = shown;
        _hasMore = hasMore;
        Message = shown.Count == 0 ? "No commits yet." : string.Empty;
        Rebuild();
    }

    /// <summary>Opens or closes a commit's list of changed files.</summary>
    public async Task ToggleAsync(HistoryCommitRow row)
    {
        var hash = row.Commit.Hash;
        if (!_expanded.Remove(hash))
        {
            if (!_files.ContainsKey(hash) && _repository() is { } repository)
            {
                try
                {
                    _files[hash] = await repository.CommitChangesAsync(row.Commit);
                }
                catch (GitException exception)
                {
                    Message = exception.Message;
                    return;
                }
            }

            _expanded.Add(hash);
        }

        Rebuild();
    }

    /// <summary>Shows a file's change in a commit, side by side with its first parent's version.</summary>
    public Task OpenAsync(HistoryFileRow row) => OpenChangeAsync(row.Owner.Commit, row.Change);

    /// <summary>The commits that changed a file, newest first (for "View File History").</summary>
    public async Task<IReadOnlyList<GitCommit>> FileLogAsync(string path) =>
        _repository() is { } repository ? await repository.FileLogAsync(path, 500) : [];

    /// <summary>Shows what <paramref name="commit"/> changed in the file at <paramref name="path"/>.</summary>
    public Task OpenFileAtAsync(GitCommit commit, string path) =>
        _repository() is { } repository
            ? OpenChangeAsync(commit, new GitChange(path, Path.GetRelativePath(repository.Root, path).Replace('\\', '/'), GitChangeKind.Modified))
            : Task.CompletedTask;

    private async Task OpenChangeAsync(GitCommit commit, GitChange change)
    {
        if (_repository() is not { } repository)
        {
            return;
        }

        var original = commit.Parent is { } parent && change.Kind != GitChangeKind.Added
            ? await repository.ShowFileAsync(change.OriginalPath ?? change.Path, parent) ?? string.Empty
            : string.Empty;
        var modified = change.Kind == GitChangeKind.Deleted ? string.Empty : await repository.ShowFileAsync(change.Path, commit.Hash) ?? string.Empty;
        if (original.Contains('\0', StringComparison.Ordinal) || modified.Contains('\0', StringComparison.Ordinal))
        {
            Message = $"{change.FileName} is a binary file and can't be compared as text.";
            return;
        }

        var parentLabel = commit.Parent is { } p ? p[..Math.Min(7, p.Length)] : "Empty";
        _editors.OpenDiff(new DiffTabViewModel(change.Path, parentLabel, original, commit.ShortHash, modified));
    }

    /// <summary>Forgets everything about the previous repository (another folder was opened).</summary>
    public void Reset()
    {
        _count = PageSize;
        _expanded.Clear();
        _files.Clear();
        Clear(string.Empty);
        _ = RefreshAsync();
    }

    private void Clear(string message)
    {
        _commits = [];
        _hasMore = false;
        Message = message;
        Rows.Clear();
    }

    private void Rebuild()
    {
        var graph = GitGraph.Layout(_commits);
        var head = _commits.FirstOrDefault(c => c.Refs.Any(r => r == "HEAD" || r.StartsWith("HEAD -> ", StringComparison.Ordinal)))?.Hash;
        var now = DateTimeOffset.Now;
        _expanded.IntersectWith(_commits.Select(c => c.Hash));

        Rows.Clear();
        for (var i = 0; i < _commits.Count; i++)
        {
            var commit = _commits[i];
            var expanded = _expanded.Contains(commit.Hash);
            var row = new HistoryCommitRow(commit, graph[i], commit.Hash == head, expanded, RelativeTime.Format(commit.Date, now));
            Rows.Add(row);
            if (expanded && _files.TryGetValue(commit.Hash, out var files))
            {
                foreach (var file in files)
                {
                    Rows.Add(new HistoryFileRow(row, file));
                }
            }
        }

        if (_hasMore)
        {
            Rows.Add(new HistoryMoreRow());
        }
    }

    /// <summary>Same commit and same branch labels (a moved branch changes the labels, not the commit).</summary>
    private sealed class SameCommit : IEqualityComparer<GitCommit>
    {
        public static readonly SameCommit Instance = new();

        public bool Equals(GitCommit? x, GitCommit? y) =>
            x is not null && y is not null && x.Hash == y.Hash && x.Refs.SequenceEqual(y.Refs);

        public int GetHashCode(GitCommit obj) => obj.Hash.GetHashCode(StringComparison.Ordinal);
    }
}
