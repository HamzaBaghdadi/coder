using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Coder.Core.Git;

/// <summary>A git command failed; the message is git's own explanation.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// A working copy, operated through the <c>git</c> command line (like VS Code): every feature and
/// configuration of the user's git applies, and nothing native is loaded into the editor.
/// </summary>
public sealed partial class GitRepository
{
    private GitRepository(string root) => Root = root;

    public string Root { get; }

    /// <summary>True when a <c>git</c> executable can be found.</summary>
    public static bool IsGitInstalled => Lsp.ExecutableLocator.Find("git") is not null;

    /// <summary>The repository containing <paramref name="folder"/> (walking up to the one with <c>.git</c>), or null.</summary>
    public static GitRepository? Open(string folder)
    {
        for (var current = Path.GetFullPath(folder); current is not null; current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) // a file in worktrees and submodules
            {
                return new GitRepository(Path.TrimEndingDirectorySeparator(current));
            }
        }

        return null;
    }

    public async Task<GitStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], cancellationToken);
        return GitStatus.Parse(output, Root);
    }

    public Task StageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(["add", "-A", "--", .. Relative(paths)], cancellationToken);

    /// <summary>Takes files out of the index. Before the first commit there is no HEAD to restore from, so they are just removed from the index.</summary>
    public async Task UnstageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var relative = Relative(paths).ToList();
        if (await HasCommitsAsync(cancellationToken))
        {
            await RunAsync(["restore", "--staged", "--", .. relative], cancellationToken);
        }
        else
        {
            await RunAsync(["rm", "--cached", "-r", "-q", "--", .. relative], cancellationToken);
        }
    }

    private async Task<bool> HasCommitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws away working tree changes: tracked files return to their staged (or committed) content,
    /// untracked files are deleted. Staged changes are kept, as in VS Code.
    /// </summary>
    public async Task DiscardAsync(IReadOnlyList<GitChange> changes, CancellationToken cancellationToken = default)
    {
        var untracked = changes.Where(c => c.Kind == GitChangeKind.Untracked).Select(c => c.Path).ToList();
        var tracked = changes.Where(c => c.Kind != GitChangeKind.Untracked).Select(c => c.Path).ToList();
        if (tracked.Count > 0)
        {
            await RunAsync(["restore", "--worktree", "--", .. Relative(tracked)], cancellationToken);
        }

        if (untracked.Count > 0)
        {
            await RunAsync(["clean", "-f", "-q", "--", .. Relative(untracked)], cancellationToken);
        }
    }

    /// <summary>
    /// A file's content at a revision: <c>HEAD</c>, or an empty revision for the index (staged version).
    /// Null when the file does not exist there (new or untracked).
    /// </summary>
    public async Task<string?> ShowFileAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        var relative = Relative([path]).Single();
        try
        {
            return await RunAsync(["show", $"{revision}:{relative}"], cancellationToken);
        }
        catch (GitException)
        {
            return null;
        }
    }

    public Task CommitAsync(string message, CancellationToken cancellationToken = default) =>
        RunAsync(["commit", "--quiet", "--cleanup=strip", "-m", message], cancellationToken);

    /// <summary>
    /// Puts <paramref name="content"/> into the index as the file's staged version, leaving the working
    /// tree alone: how a single change is staged. Git's filters (line endings) apply as for "git add".
    /// </summary>
    public async Task StageContentAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var relative = Relative([path]).Single();
        var entry = (await RunAsync(["ls-files", "-s", "--", relative], cancellationToken)).Split(' ', 2)[0];
        var mode = entry.Length > 0 ? entry : "100644";
        var blob = (await RunAsync(["hash-object", "-w", "--stdin", "--path", relative], cancellationToken, input: content)).Trim();
        await RunAsync(["update-index", "--add", "--cacheinfo", $"{mode},{blob},{relative}"], cancellationToken);
    }

    // Stashes -----------------------------------------------------------------------------------

    /// <summary>Stashes every change, untracked files included, under an optional message.</summary>
    public Task StashAsync(string? message, CancellationToken cancellationToken = default) =>
        RunAsync(string.IsNullOrWhiteSpace(message)
            ? ["stash", "push", "--include-untracked"]
            : ["stash", "push", "--include-untracked", "-m", message], cancellationToken);

    public async Task<IReadOnlyList<GitStash>> ListStashesAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(["stash", "list", "--format=%gd%x00%gs%x00%cr"], cancellationToken);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\0'))
            .Where(fields => fields.Length >= 3)
            .Select(fields => new GitStash(fields[0], fields[1], fields[2]))
            .ToList();
    }

    /// <summary>Restores a stash's changes; <paramref name="drop"/> also removes it ("pop").</summary>
    public Task ApplyStashAsync(GitStash stash, bool drop, CancellationToken cancellationToken = default) =>
        RunAsync(["stash", drop ? "pop" : "apply", stash.Reference], cancellationToken);

    public Task DropStashAsync(GitStash stash, CancellationToken cancellationToken = default) =>
        RunAsync(["stash", "drop", stash.Reference], cancellationToken);

    // Branches and remotes ------------------------------------------------------------------------

    /// <summary>Local branches, then remote branches (without the remotes' symbolic HEADs).</summary>
    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            ["for-each-ref", "--format=%(HEAD)%00%(refname)%00%(refname:short)%00%(upstream:short)%00%(contents:subject)", "refs/heads", "refs/remotes"],
            cancellationToken);
        var branches = new List<GitBranch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 5 || fields[1].EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var isRemote = fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal);
            branches.Add(new GitBranch(fields[2], isRemote, fields[0] == "*", fields[3].Length > 0 ? fields[3] : null, fields[4]));
        }

        return branches.OrderBy(b => b.IsRemote).ThenByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken cancellationToken = default) =>
        (await RunAsync(["remote"], cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Switches to a local branch, or to a remote branch through a new local branch that tracks it.</summary>
    public Task SwitchAsync(GitBranch branch, CancellationToken cancellationToken = default) =>
        branch.IsRemote
            ? RunAsync(["switch", "--track", branch.Name], cancellationToken)
            : RunAsync(["switch", branch.Name], cancellationToken);

    /// <summary>Creates a branch at the current commit and switches to it.</summary>
    public Task CreateBranchAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["switch", "-c", name], cancellationToken);

    /// <summary>True when git accepts <paramref name="name"/> as a new branch name.</summary>
    public async Task<bool> IsValidBranchNameAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunAsync(["check-ref-format", "--branch", name], cancellationToken);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>
    /// The commit message git prepared for the merge in progress, without its comment lines; null when no
    /// merge is in progress.
    /// </summary>
    public async Task<string?> MergeMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(await GitPathAsync("MERGE_HEAD", cancellationToken)))
        {
            return null;
        }

        var file = await GitPathAsync("MERGE_MSG", cancellationToken);
        if (!File.Exists(file))
        {
            return string.Empty;
        }

        var lines = (await File.ReadAllLinesAsync(file, cancellationToken)).Where(line => !line.StartsWith('#'));
        return string.Join('\n', lines).Trim();
    }

    /// <summary>Stops the merge in progress and puts the branch back as it was before it.</summary>
    public Task AbortMergeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["merge", "--abort"], cancellationToken);

    /// <summary>A file inside the git directory (also right for worktrees, where .git is a file).</summary>
    private async Task<string> GitPathAsync(string name, CancellationToken cancellationToken) =>
        Path.GetFullPath((await RunAsync(["rev-parse", "--git-path", name], cancellationToken)).Trim(), Root);

    public Task FetchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["fetch", "--all", "--prune"], cancellationToken);

    /// <summary>Pulls the upstream branch. The merge commit, when one is needed, keeps git's default message.</summary>
    public Task PullAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["pull", "--no-edit"], cancellationToken);

    /// <summary>Pushes the current branch; a branch without an upstream is published to <paramref name="remote"/> and tracks it.</summary>
    public Task PushAsync(string? branch, string? remote, CancellationToken cancellationToken = default) =>
        remote is not null && branch is not null
            ? RunAsync(["push", "--set-upstream", remote, branch], cancellationToken)
            : RunAsync(["push"], cancellationToken);

    private IEnumerable<string> Relative(IEnumerable<string> paths) =>
        paths.Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'));

    /// <summary>Runs git in the repository and returns its output; a non-zero exit throws with git's message.</summary>
    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo(Lsp.ExecutableLocator.Find("git") ?? "git")
        {
            WorkingDirectory = Root,
            RedirectStandardInput = true, // closed at once: a prompt (credentials, SSH host keys) fails instead of waiting forever
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Paths as UTF-8 without quoting; status must not take locks that would block the user's own git.
        foreach (var argument in (string[])["-c", "core.quotepath=false", "-c", "color.ui=false", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new GitException("Could not start git.");
        }
        catch (Win32Exception exception)
        {
            throw new GitException($"Could not start git: {exception.Message}");
        }

        using (process)
        {
            // Read while writing: a large input could otherwise fill the pipes both ways and stall.
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Git stopped reading (it failed early, e.g. blame of an untracked file): its exit code and
                // message below say why. Closing flushes what is left, which fails the same way.
                try
                {
                    process.StandardInput.Dispose();
                }
                catch (IOException)
                {
                    // The pipe is closed now either way.
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                // Some explanations ("nothing to commit") go to stdout.
                var message = (await error).Trim() is { Length: > 0 } stderr ? stderr : (await output).Trim();
                throw new GitException(message.Length > 0 ? message : $"git {arguments[0]} failed with exit code {process.ExitCode}.");
            }

            return await output;
        }
    }
}
