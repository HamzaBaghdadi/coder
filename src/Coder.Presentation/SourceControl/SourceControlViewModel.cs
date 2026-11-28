using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Coder.Core.Git;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Explorer;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Services;

namespace Coder.Presentation.SourceControl;

public enum SourceControlGroup
{
    Conflicts,
    Staged,
    Changes,
}

/// <summary>A row of the Source Control list: a group header or a changed file.</summary>
public abstract record SourceControlRow(SourceControlGroup Group);

public sealed record SourceControlGroupRow(SourceControlGroup Group, string Title, int Count) : SourceControlRow(Group)
{
    /// <summary>Whether the group's files are listed under it (clicking the header folds it, like VS Code).</summary>
    public bool IsExpanded { get; init; } = true;
}

public sealed record SourceControlFileRow(SourceControlGroup Group, GitChange Change) : SourceControlRow(Group)
{
    /// <summary>The folder part of the path, shown dimmed after the file name.</summary>
    public string Folder => Path.GetDirectoryName(Change.RelativePath)?.Replace('\\', '/') ?? string.Empty;
}

/// <summary>
/// Source Control for the open folder's git repository: the branch, the changes grouped as merge
/// conflicts, staged and working tree changes, and the commands to stage, unstage, discard and commit.
/// It refreshes itself when files change on disk (debounced), when a document is saved and after
/// every operation, and colors the explorer's rows to match.
/// </summary>
public sealed partial class SourceControlViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);

    private readonly ExplorerViewModel _explorer;
    private readonly EditorGroupViewModel _editors;
    private readonly IDialogService _dialogs;
    private readonly Func<string, Task> _openFile;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Timer _refreshTimer;
    private readonly QuickDiff _quickDiff;
    private GitRepository? _repository;
    private GitStatus? _status;
    private readonly HashSet<SourceControlGroup> _collapsed = [];
    private FileSystemWatcher? _watcher;
    private string _commitMessage = string.Empty;
    private string _message = string.Empty;
    private bool _isBusy;
    private bool _isMerging;
    private Task _refresh = Task.CompletedTask;
    private bool _refreshAgain;

    public SourceControlViewModel(ExplorerViewModel explorer, EditorGroupViewModel editors, IDialogService dialogs, Func<string, Task> openFile)
    {
        _explorer = explorer;
        _editors = editors;
        _dialogs = dialogs;
        _openFile = openFile;
        _refreshTimer = new Timer(_ => Post(() => _ = RefreshAsync()));
        _quickDiff = new QuickDiff(editors, () => _repository);
        History = new HistoryViewModel(() => _repository, () => _status, editors);
        Blame = new BlameViewModel(editors, () => _repository);

        explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.RootPath))
            {
                OpenRepository(explorer.RootPath);
            }
        };
        explorer.Rows.CollectionChanged += OnExplorerRowsChanged;
        editors.Saved += (_, _) => ScheduleRefresh();
        OpenRepository(explorer.RootPath);
    }

    /// <summary>Raised after every refresh, so other views can follow the status.</summary>
    public event EventHandler? StatusChanged;

    public ObservableCollection<SourceControlRow> Rows { get; } = [];

    /// <summary>The commit history shown under the changes.</summary>
    public HistoryViewModel History { get; }

    /// <summary>Who last changed the line under the caret.</summary>
    public BlameViewModel Blame { get; }

    public bool HasRepository => _repository is not null;

    public GitStatus? Status => _status;

    /// <summary>The branch name, or "detached HEAD"; empty outside a repository.</summary>
    public string BranchText => _status is null ? string.Empty : _status.Branch ?? "detached HEAD";

    public int ChangeCount => _status?.ChangeCount ?? 0;

    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    public string CommitPlaceholder => _status?.Branch is { } branch ? $"Message (Ctrl+Enter to commit on \"{branch}\")" : "Message (Ctrl+Enter to commit)";

    /// <summary>The last problem (a git error), shown above the list until the next operation.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    // Commands ----------------------------------------------------------------------------------

    public async Task StageAsync(SourceControlFileRow row)
    {
        if (await ConfirmStagingConflictsAsync([row.Change]))
        {
            await RunAsync(repository => repository.StageAsync([row.Change.Path]));
        }
    }

    public Task UnstageAsync(SourceControlFileRow row) => RunAsync(repository => repository.UnstageAsync([row.Change.Path]));

    public async Task StageAllAsync()
    {
        var changes = ChangesIn(SourceControlGroup.Changes).Concat(ChangesIn(SourceControlGroup.Conflicts)).ToList();
        if (await ConfirmStagingConflictsAsync(changes))
        {
            await RunAsync(repository => repository.StageAsync(changes.Select(c => c.Path)));
        }
    }

    /// <summary>
    /// Staging a conflicted file marks it resolved. When it still has conflict markers (in its open tab
    /// or on disk), that is almost always a mistake, so ask first, as VS Code does.
    /// </summary>
    private async Task<bool> ConfirmStagingConflictsAsync(IEnumerable<GitChange> changes)
    {
        var unresolved = new List<string>();
        foreach (var change in changes.Where(c => c.Kind == GitChangeKind.Conflicted))
        {
            var text = _editors.FindTab(change.Path)?.Document.GetText()
                       ?? (File.Exists(change.Path) ? await File.ReadAllTextAsync(change.Path) : string.Empty);
            if (MergeConflicts.Find(new TextDocument(text)).Count > 0)
            {
                unresolved.Add(change.FileName);
            }
        }

        return unresolved.Count == 0 || await _dialogs.ConfirmAsync(
            unresolved.Count == 1 ? $"\"{unresolved[0]}\" still has merge conflicts." : $"{unresolved.Count} files still have merge conflicts.",
            "Staging marks the conflicts as resolved, with the conflict markers left in the file. Stage anyway?",
            "Stage Anyway");
    }

    public Task UnstageAllAsync() => RunAsync(repository => repository.UnstageAsync(ChangesIn(SourceControlGroup.Staged).Select(c => c.Path)));

    /// <summary>Discards one file's working tree changes, after confirming (deleting an untracked file cannot be undone).</summary>
    public async Task DiscardAsync(SourceControlFileRow row)
    {
        var change = row.Change;
        var confirmed = change.Kind == GitChangeKind.Untracked
            ? await _dialogs.ConfirmAsync($"Delete \"{change.FileName}\"?", "The file is not tracked by git, so it is deleted permanently. This cannot be undone.", "Delete File")
            : await _dialogs.ConfirmAsync($"Discard changes in \"{change.FileName}\"?", "Your changes to this file are lost. This cannot be undone.", "Discard Changes");
        if (confirmed)
        {
            await RunAsync(repository => repository.DiscardAsync([change]));
        }
    }

    public async Task DiscardAllAsync()
    {
        var changes = ChangesIn(SourceControlGroup.Changes).ToList();
        if (changes.Count > 0 && await _dialogs.ConfirmAsync(
                $"Discard all {changes.Count} changes?",
                "Changes to tracked files are lost and untracked files are deleted. Staged changes are kept. This cannot be undone.",
                "Discard All"))
        {
            await RunAsync(repository => repository.DiscardAsync(changes));
        }
    }

    /// <summary>
    /// Commits the staged changes. With nothing staged, offers to stage everything first (like VS Code).
    /// </summary>
    public async Task CommitAsync()
    {
        if (_repository is null || _status is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            Message = "Write a commit message first.";
            return;
        }

        if (_status.Conflicts.Count > 0)
        {
            Message = "Resolve the merge conflicts and stage the files before committing.";
            return;
        }

        var stageAll = false;
        if (_status.Staged.Count == 0)
        {
            if (_status.Unstaged.Count == 0)
            {
                Message = "There are no changes to commit.";
                return;
            }

            stageAll = await _dialogs.ConfirmAsync(
                "There are no staged changes to commit.",
                "Stage all your changes and commit them directly?",
                "Stage All and Commit");
            if (!stageAll)
            {
                return;
            }
        }

        var message = CommitMessage;
        var unstaged = _status.Unstaged.Select(c => c.Path).ToList();
        if (await RunAsync(async repository =>
            {
                if (stageAll)
                {
                    await repository.StageAsync(unstaged);
                }

                await repository.CommitAsync(message);
            }))
        {
            CommitMessage = string.Empty;
        }
    }

    /// <summary>
    /// Shows what changed in a file, as VS Code does: a working tree change against the index (with
    /// unsaved edits included), a staged change against HEAD. Conflicts open the file itself.
    /// </summary>
    public async Task OpenChangesAsync(SourceControlFileRow row)
    {
        if (_repository is not { } repository)
        {
            return;
        }

        var change = row.Change;
        if (row.Group == SourceControlGroup.Conflicts)
        {
            await OpenAsync(row);
            return;
        }

        try
        {
            var staged = row.Group == SourceControlGroup.Staged;
            var original = change.Kind is GitChangeKind.Untracked or GitChangeKind.Added
                ? string.Empty
                : await repository.ShowFileAsync(change.OriginalPath ?? change.Path, staged ? "HEAD" : string.Empty) ?? string.Empty;
            var modified = change.Kind == GitChangeKind.Deleted
                ? string.Empty
                : staged
                    ? await repository.ShowFileAsync(change.Path, string.Empty) ?? string.Empty
                    : _editors.FindTab(change.Path)?.Document.GetText() ?? await File.ReadAllTextAsync(change.Path);
            if (original.Contains('\0', StringComparison.Ordinal) || modified.Contains('\0', StringComparison.Ordinal))
            {
                Message = $"{change.FileName} is a binary file and can't be compared as text.";
                return;
            }

            _editors.OpenDiff(new DiffTabViewModel(change.Path, staged ? "HEAD" : "Index", original, staged ? "Index" : "Working Tree", modified));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or GitException)
        {
            Message = exception.Message;
        }
    }

    public Task OpenAsync(SourceControlFileRow row) =>
        row.Change.Kind == GitChangeKind.Deleted ? Task.CompletedTask : _openFile(row.Change.Path);

    /// <summary>
    /// Reads <c>git status</c> again. A call during a refresh is folded into one more pass of it, and
    /// waits for that pass: when this returns, the status reflects everything done before the call.
    /// </summary>
    public Task RefreshAsync()
    {
        if (_repository is not { } repository)
        {
            return Task.CompletedTask;
        }

        if (!_refresh.IsCompleted)
        {
            _refreshAgain = true;
            return _refresh;
        }

        _refresh = RefreshLoopAsync(repository);
        return _refresh;
    }

    private async Task RefreshLoopAsync(GitRepository repository)
    {
        try
        {
            do
            {
                _refreshAgain = false;
                var status = await repository.StatusAsync();
                var mergeMessage = await repository.MergeMessageAsync();
                await LoadRemotesAsync(repository);
                if (!ReferenceEquals(repository, _repository))
                {
                    return; // another folder was opened meanwhile
                }

                Apply(status);
                _ = History.RefreshAsync();
                Blame.Refresh();
                ApplyMerge(mergeMessage);
            }
            while (_refreshAgain);
        }
        catch (GitException exception)
        {
            Message = exception.Message;
        }
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
        Blame.Dispose();
        _watcher?.Dispose();
        _quickDiff.Dispose();
    }

    // Repository and status ---------------------------------------------------------------------

    private void OpenRepository(string? folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _repository = folder is not null && GitRepository.IsGitInstalled ? GitRepository.Open(folder) : null;
        _status = null;
        Rows.Clear();
        History.Reset();
        Message = string.Empty;
        OnPropertyChanged(string.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (_repository is null)
        {
            DecorateExplorer(_explorer.Rows);
            _quickDiff.Reload();
            return;
        }

        Watch(_repository.Root);
        _ = RefreshAsync();
    }

    /// <summary>Any file change in the working tree (or the index, HEAD and refs) triggers a debounced refresh.</summary>
    private void Watch(string root)
    {
        try
        {
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (IsRelevant(root, e.FullPath))
                {
                    ScheduleRefresh();
                }
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, e) => onChange(this, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null; // refreshing on save and after operations still works
        }
    }

    /// <summary>Inside .git only the index, HEAD and refs matter; objects, logs and lock files churn constantly.</summary>
    private static bool IsRelevant(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!relative.StartsWith(".git/", StringComparison.Ordinal) && relative != ".git")
        {
            return true;
        }

        return !relative.EndsWith(".lock", StringComparison.Ordinal)
               && (relative is ".git/index" or ".git/HEAD" or ".git/config" || relative.StartsWith(".git/refs/", StringComparison.Ordinal));
    }

    private void ScheduleRefresh()
    {
        if (_repository is not null)
        {
            _refreshTimer.Change(RefreshDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Apply(GitStatus status)
    {
        _status = status;
        RebuildRows(status);
        OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CommitPlaceholder));
        OnPropertyChanged(nameof(Status));
        DecorateExplorer(_explorer.Rows);
        _quickDiff.Reload(); // the index may have changed (stage, commit, checkout)
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// While a merge is in progress, the commit box starts with git's merge message (once: clearing it
    /// keeps it clear), as VS Code does.
    /// </summary>
    private void ApplyMerge(string? mergeMessage)
    {
        var wasMerging = IsMerging;
        IsMerging = mergeMessage is not null;
        if (IsMerging && !wasMerging && CommitMessage.Length == 0)
        {
            CommitMessage = mergeMessage!;
        }
    }

    /// <summary>Whether a merge is in progress (it can be committed once conflicts are resolved, or aborted).</summary>
    public bool IsMerging
    {
        get => _isMerging;
        private set => SetProperty(ref _isMerging, value);
    }

    /// <summary>Gives up the merge in progress, after confirming: the branch goes back to how it was before it.</summary>
    public async Task AbortMergeAsync()
    {
        if (!IsMerging)
        {
            Message = "There is no merge in progress.";
            return;
        }

        if (await _dialogs.ConfirmAsync("Abort the merge?", "The merge's changes and your conflict resolutions are lost.", "Abort Merge")
            && await RunAsync(repository => repository.AbortMergeAsync(), "Aborting merge…"))
        {
            CommitMessage = string.Empty;
        }
    }

    /// <summary>Empty groups are hidden, except "Changes", which always shows (like VS Code).</summary>
    private void AddGroup(SourceControlGroup group, string title, IReadOnlyList<GitChange> changes)
    {
        if (changes.Count == 0 && group != SourceControlGroup.Changes)
        {
            return;
        }

        var expanded = !_collapsed.Contains(group);
        Rows.Add(new SourceControlGroupRow(group, title, changes.Count) { IsExpanded = expanded });
        if (!expanded)
        {
            return; // folded: the header keeps its count and actions, the files are hidden
        }

        foreach (var change in changes.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new SourceControlFileRow(group, change));
        }
    }

    /// <summary>Folds or unfolds a group (Merge Changes, Staged Changes, Changes); it stays so across refreshes.</summary>
    public void ToggleGroup(SourceControlGroup group)
    {
        if (!_collapsed.Remove(group))
        {
            _collapsed.Add(group);
        }

        if (_status is not null)
        {
            RebuildRows(_status);
        }
    }

    private void RebuildRows(GitStatus status)
    {
        Rows.Clear();
        AddGroup(SourceControlGroup.Conflicts, "Merge Changes", status.Conflicts);
        AddGroup(SourceControlGroup.Staged, "Staged Changes", status.Staged);
        AddGroup(SourceControlGroup.Changes, "Changes", status.Unstaged);
    }

    /// <summary>A group's changes from the status (not the rows, which leave out the files of folded groups).</summary>
    private IEnumerable<GitChange> ChangesIn(SourceControlGroup group) => group switch
    {
        SourceControlGroup.Conflicts => _status?.Conflicts ?? [],
        SourceControlGroup.Staged => _status?.Staged ?? [],
        _ => _status?.Unstaged ?? [],
    };

    /// <summary>Runs a git operation, then refreshes. Errors become <see cref="Message"/>. Returns whether it succeeded.</summary>
    private async Task<bool> RunAsync(Func<GitRepository, Task> operation, string? progress = null)
    {
        if (_repository is not { } repository || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        ProgressText = progress ?? string.Empty;
        Message = string.Empty;
        try
        {
            await operation(repository);
            return true;
        }
        catch (GitException exception)
        {
            Message = exception.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            await RefreshAsync();
        }
    }

    // Explorer decorations ----------------------------------------------------------------------

    private void OnExplorerRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DecorateExplorer(e.NewItems?.OfType<ExplorerItemViewModel>() ?? []);

    private void DecorateExplorer(IEnumerable<ExplorerItemViewModel> rows)
    {
        foreach (var row in rows)
        {
            row.GitChange = row.IsDirectory ? null : _status?.KindOf(row.Path);
            row.ContainsGitChanges = row.IsDirectory && _status?.ContainsChanges(row.Path) == true;
        }
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
