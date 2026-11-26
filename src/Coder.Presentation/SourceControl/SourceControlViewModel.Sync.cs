using System.Text.RegularExpressions;
using Coder.Core.Diff;
using Coder.Core.Git;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.Presentation.SourceControl;

/// <summary>Branches and remotes: switching and creating branches, fetch, pull, push and sync.</summary>
public sealed partial class SourceControlViewModel
{
    private IReadOnlyList<string> _remotes = [];
    private string _progressText = string.Empty;

    /// <summary>What a long operation is doing ("Pushing…"), for the status bar; empty when idle.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool HasRemotes => _remotes.Count > 0;

    /// <summary>
    /// The sync button's text: "2↓ 1↑" against the upstream, "Publish Branch" for a branch that has none
    /// yet, or empty when there is nothing to sync with.
    /// </summary>
    public string SyncText
    {
        get
        {
            if (_status?.Branch is null || !HasRemotes)
            {
                return string.Empty;
            }

            return NeedsPublish ? "Publish Branch" : $"{_status.Behind}↓ {_status.Ahead}↑";
        }
    }

    /// <summary>No upstream, or one that does not exist on the remote (yet): pushing must publish.</summary>
    private bool NeedsPublish => _status is { } status && (status.Upstream is null || status.IsUpstreamGone);

    /// <summary>
    /// What the Source Control panel's main button does, like VS Code: commit while there are changes;
    /// otherwise publish a branch the remote does not have, or sync when there are commits to pull or push.
    /// </summary>
    public SourceControlAction PrimaryAction =>
        ChangeCount > 0 ? SourceControlAction.Commit
        : _status?.Branch is not null && HasRemotes && NeedsPublish ? SourceControlAction.Publish
        : _status is { Ahead: > 0 } or { Behind: > 0 } && !NeedsPublish ? SourceControlAction.Sync
        : SourceControlAction.Commit;

    public string PrimaryActionText => PrimaryAction switch
    {
        SourceControlAction.Publish => "Publish Branch",
        SourceControlAction.Sync => $"Sync Changes {_status!.Behind}↓ {_status.Ahead}↑",
        _ => "Commit",
    };

    public Task RunPrimaryActionAsync() => PrimaryAction switch
    {
        SourceControlAction.Publish => PushAsync(),
        SourceControlAction.Sync => SyncAsync(),
        _ => CommitAsync(),
    };

    // Single changes (from the gutter) ------------------------------------------------------------

    /// <summary>Stages one change of an open file, leaving its other changes unstaged.</summary>
    public Task StageChangeAsync(EditorTabViewModel tab, DiffHunk hunk)
    {
        if (tab.FilePath is not { } path)
        {
            return Task.CompletedTask;
        }

        var current = LineDiff.SplitLines(tab.Document.GetText());
        var staged = HunkOperations.Stage(tab.ChangeBase, current, hunk);
        var content = LineEndings.Apply(string.Join('\n', staged), tab.Document.LineEnding);
        return RunAsync(repository => repository.StageContentAsync(path, content), "Staging change…");
    }

    /// <summary>
    /// Puts one change back to its staged text, in the editor (one undo step, unsaved like any edit).
    /// Only the differing middle is replaced, so the caret and the rest of the file stay put.
    /// </summary>
    public static void RevertChange(EditorTabViewModel tab, DiffHunk hunk) =>
        tab.Editor.ReplaceText(string.Join('\n', HunkOperations.Revert(tab.ChangeBase, LineDiff.SplitLines(tab.Document.GetText()), hunk)));

    // Stashes ---------------------------------------------------------------------------------------

    public Task StashAsync(string? message) => RunAsync(repository => repository.StashAsync(message), "Stashing…");

    public async Task<IReadOnlyList<GitStash>> ListStashesAsync()
    {
        try
        {
            return _repository is { } repository ? await repository.ListStashesAsync() : [];
        }
        catch (GitException exception)
        {
            Message = exception.Message;
            return [];
        }
    }

    public Task ApplyStashAsync(GitStash stash, bool drop) =>
        RunAsync(repository => repository.ApplyStashAsync(stash, drop), drop ? "Popping stash…" : "Applying stash…");

    public Task DropStashAsync(GitStash stash) => RunAsync(repository => repository.DropStashAsync(stash), "Dropping stash…");

    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync()
    {
        if (_repository is not { } repository)
        {
            return [];
        }

        try
        {
            return await repository.ListBranchesAsync();
        }
        catch (GitException exception)
        {
            Message = exception.Message;
            return [];
        }
    }

    public Task SwitchBranchAsync(GitBranch branch) =>
        RunAsync(repository => repository.SwitchAsync(branch), $"Switching to {branch.Name}…");

    /// <summary>Creates a branch from the current commit and switches to it. Spaces become dashes, as in VS Code.</summary>
    public async Task CreateBranchAsync(string name)
    {
        if (_repository is not { } repository)
        {
            return;
        }

        var branch = BranchName(name);
        if (branch.Length == 0 || !await repository.IsValidBranchNameAsync(branch))
        {
            Message = $"\"{name}\" is not a valid branch name.";
            return;
        }

        await RunAsync(r => r.CreateBranchAsync(branch), $"Creating {branch}…");
    }

    /// <summary>Turns typed text into a branch name: trimmed, with runs of whitespace as single dashes.</summary>
    public static string BranchName(string text) => Whitespace().Replace(text.Trim(), "-");

    public Task FetchAsync() => RunAsync(repository => repository.FetchAsync(), "Fetching…");

    public Task PullAsync() => RunAsync(repository => repository.PullAsync(), "Pulling…");

    /// <summary>Pushes; a branch without an upstream is published (to "origin", or the only remote).</summary>
    public Task PushAsync()
    {
        if (_status is not null && !NeedsPublish)
        {
            return RunAsync(repository => repository.PushAsync(branch: null, remote: null), "Pushing…");
        }

        if (_status?.Branch is not { } branch)
        {
            Message = "Check out a branch before pushing.";
            return Task.CompletedTask;
        }

        if (!HasRemotes)
        {
            Message = "This repository has no remote to push to. Add one with \"git remote add\".";
            return Task.CompletedTask;
        }

        // A gone upstream names its remote ("origin/main"); otherwise prefer "origin".
        var upstreamRemote = _status?.Upstream?.Split('/')[0];
        var remote = upstreamRemote is not null && _remotes.Contains(upstreamRemote) ? upstreamRemote : _remotes.Contains("origin") ? "origin" : _remotes[0];
        return RunAsync(repository => repository.PushAsync(branch, remote), $"Publishing {branch}…");
    }

    /// <summary>The status bar's sync button: pull then push, or publish a new branch.</summary>
    public async Task SyncAsync()
    {
        if (NeedsPublish)
        {
            await PushAsync();
            return;
        }

        await RunAsync(async repository =>
        {
            await repository.PullAsync();
            await repository.PushAsync(branch: null, remote: null);
        }, "Syncing…");
    }

    private async Task LoadRemotesAsync(GitRepository repository)
    {
        try
        {
            _remotes = await repository.ListRemotesAsync();
        }
        catch (GitException)
        {
            _remotes = [];
        }

        OnPropertyChanged(nameof(HasRemotes));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>The Source Control panel's main button.</summary>
public enum SourceControlAction
{
    Commit,
    Publish,
    Sync,
}
