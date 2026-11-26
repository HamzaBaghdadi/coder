namespace Coder.Core.Git;

/// <summary>History: the commit log, what a commit changed, and who last changed each line (blame).</summary>
public sealed partial class GitRepository
{
    /// <summary>
    /// Commits reachable from HEAD and from its upstream (so fetched incoming commits show too), newest
    /// first with parents after their children. Empty before the first commit.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> LogAsync(int count, int skip = 0, string? upstream = null, CancellationToken cancellationToken = default)
    {
        if (!await HasCommitsAsync(cancellationToken))
        {
            return [];
        }

        List<string> arguments = ["log", "--topo-order", "-z", GitHistoryParser.LogFormat, $"--max-count={count}", $"--skip={skip}", "HEAD"];
        if (upstream is not null)
        {
            arguments.Add(upstream);
        }

        arguments.Add("--");
        return GitHistoryParser.ParseLog(await RunAsync(arguments, cancellationToken));
    }

    /// <summary>The commits that changed a file, newest first, following renames.</summary>
    public async Task<IReadOnlyList<GitCommit>> FileLogAsync(string path, int count, CancellationToken cancellationToken = default)
    {
        if (!await HasCommitsAsync(cancellationToken))
        {
            return [];
        }

        return GitHistoryParser.ParseLog(await RunAsync(
            ["log", "--follow", "-z", GitHistoryParser.LogFormat, $"--max-count={count}", "--", .. Relative([path])], cancellationToken));
    }

    /// <summary>The files a commit changed, compared with its first parent (the first commit: with nothing).</summary>
    public async Task<IReadOnlyList<GitChange>> CommitChangesAsync(GitCommit commit, CancellationToken cancellationToken = default)
    {
        var output = commit.Parent is { } parent
            ? await RunAsync(["diff", "--name-status", "-z", "-M", parent, commit.Hash, "--"], cancellationToken)
            : await RunAsync(["diff-tree", "--root", "--no-commit-id", "-r", "--name-status", "-z", "-M", commit.Hash], cancellationToken);
        return GitHistoryParser.ParseNameStatus(output, Root);
    }

    /// <summary>
    /// Who last changed each line of <paramref name="content"/> (the file as the editor has it, unsaved edits
    /// included; null blames the file on disk). Lines changed since the last commit are not committed.
    /// Empty when the file is not tracked.
    /// </summary>
    public async Task<IReadOnlyList<GitBlameLine>> BlameAsync(string path, string? content, CancellationToken cancellationToken = default)
    {
        var relative = Relative([path]).Single();
        try
        {
            var output = content is null
                ? await RunAsync(["blame", "--porcelain", "--", relative], cancellationToken)
                : await RunAsync(["blame", "--porcelain", "--contents", "-", "--", relative], cancellationToken, input: content);
            return GitHistoryParser.ParseBlame(output);
        }
        catch (GitException)
        {
            return []; // untracked, outside the repository, or no commits yet
        }
    }

    /// <summary>The configured author email (user.email), to show the user's own lines as "You".</summary>
    public async Task<string?> UserEmailAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await RunAsync(["config", "user.email"], cancellationToken)).Trim();
        }
        catch (GitException)
        {
            return null;
        }
    }
}
