using System.Diagnostics;
using Coder.Core.Git;

namespace Coder.Core.Tests;

/// <summary>Git operations against real temporary repositories (skipped when git is not installed).</summary>
public sealed class GitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-git-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git marks object files read-only
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Parses_branch_staged_unstaged_renames_conflicts_and_paths_with_spaces()
    {
        const string Output =
            "# branch.oid abc\0# branch.head feature/x\0# branch.upstream origin/feature/x\0# branch.ab +2 -1\0" +
            "1 M. N... 100644 100644 100644 h1 h2 src/a b.cs\0" +
            "1 .D N... 100644 100644 000000 h1 h2 gone.txt\0" +
            "1 MM N... 100644 100644 100644 h1 h2 both.cs\0" +
            "2 R. N... 100644 100644 100644 h1 h2 R100 new name.cs\0old name.cs\0" +
            "u UU N... 100644 100644 100644 100644 h1 h2 h3 clash.cs\0" +
            "? notes/todo.md\0";
        var root = Path.GetFullPath("/repo");

        var status = GitStatus.Parse(Output, root);

        Assert.Equal("feature/x", status.Branch);
        Assert.Equal("origin/feature/x", status.Upstream);
        Assert.Equal((2, 1), (status.Ahead, status.Behind));
        Assert.Equal(["src/a b.cs:Modified", "both.cs:Modified", "new name.cs:Renamed"], status.Staged.Select(c => $"{c.RelativePath}:{c.Kind}"));
        Assert.Equal(["gone.txt:Deleted", "both.cs:Modified", "notes/todo.md:Untracked"], status.Unstaged.Select(c => $"{c.RelativePath}:{c.Kind}"));
        Assert.Equal(Path.Combine(root, "old name.cs"), status.Staged[2].OriginalPath);
        Assert.Equal("clash.cs", Assert.Single(status.Conflicts).RelativePath);
        Assert.Equal(GitChangeKind.Untracked, status.KindOf(Path.Combine(root, "notes", "todo.md")));
        Assert.True(status.ContainsChanges(Path.Combine(root, "notes")));
        Assert.True(status.ContainsChanges(Path.Combine(root, "src")));
        Assert.False(status.ContainsChanges(Path.Combine(root, "docs")));
        Assert.Equal(7, status.ChangeCount); // both.cs is staged and changed again: two entries
    }

    [Theory]
    [InlineData("# branch.head main\0# branch.upstream origin/main\0# branch.ab +0 -0\0", false)]
    [InlineData("# branch.head main\0# branch.upstream origin/main\0", true)] // tracked branch missing on the remote
    [InlineData("# branch.head main\0", false)]                             // no upstream at all
    public void Detects_an_upstream_that_does_not_exist(string output, bool gone) =>
        Assert.Equal(gone, GitStatus.Parse(output, Path.GetFullPath("/repo")).IsUpstreamGone);

    [Fact]
    public async Task Stage_unstage_commit_and_discard_in_a_new_repository()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        var readme = Path.Combine(_root, "read me.md");
        await File.WriteAllTextAsync(readme, "hello\n", TestContext.Current.CancellationToken);
        var repository = GitRepository.Open(Path.Combine(_root, "sub", "folder"))!; // finds the root from below
        Assert.Equal(_root, repository.Root, ignoreCase: OperatingSystem.IsWindows());

        var status = await repository.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("main", status.Branch);
        Assert.Equal(GitChangeKind.Untracked, Assert.Single(status.Unstaged).Kind);

        // Before the first commit there is no HEAD: unstaging must still work.
        await repository.StageAsync([readme], TestContext.Current.CancellationToken);
        Assert.Equal(GitChangeKind.Added, Assert.Single((await repository.StatusAsync(TestContext.Current.CancellationToken)).Staged).Kind);
        await repository.UnstageAsync([readme], TestContext.Current.CancellationToken);
        Assert.Empty((await repository.StatusAsync(TestContext.Current.CancellationToken)).Staged);

        await repository.StageAsync([readme], TestContext.Current.CancellationToken);
        await repository.CommitAsync("First commit", TestContext.Current.CancellationToken);
        Assert.Equal(0, (await repository.StatusAsync(TestContext.Current.CancellationToken)).ChangeCount);
        Assert.Equal("First commit", Git("log", "-1", "--format=%s").Trim());

        // Discard: a modified file returns to its committed text, a new file is deleted.
        await File.WriteAllTextAsync(readme, "changed\n", TestContext.Current.CancellationToken);
        var scratch = Path.Combine(_root, "scratch.txt");
        await File.WriteAllTextAsync(scratch, "temp", TestContext.Current.CancellationToken);
        status = await repository.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(GitChangeKind.Modified, status.KindOf(readme));
        await repository.DiscardAsync(status.Unstaged, TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", (await File.ReadAllTextAsync(readme, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"));
        Assert.False(File.Exists(scratch));
        Assert.Equal(0, (await repository.StatusAsync(TestContext.Current.CancellationToken)).ChangeCount);
    }

    /// <summary>A bare repository in the temp folder plays the remote, so push and pull run for real, offline.</summary>
    [Fact]
    public async Task Branches_publish_push_and_pull_through_a_remote()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        var remote = Path.Combine(_root, "remote.git");
        var mine = Path.Combine(_root, "mine");
        var theirs = Path.Combine(_root, "theirs");
        Git("init", "-q", "--bare", "-b", "main", remote);
        Git("clone", "-q", remote, mine);
        Configure(mine);
        await File.WriteAllTextAsync(Path.Combine(mine, "a.txt"), "1\n", token);
        var repository = GitRepository.Open(mine)!;
        await repository.StageAsync([Path.Combine(mine, "a.txt")], token);
        await repository.CommitAsync("first", token);

        // Publish: the new branch has no upstream yet, so push sets it.
        Assert.Equal(["origin"], await repository.ListRemotesAsync(token));
        await repository.PushAsync("main", "origin", token);
        var status = await repository.StatusAsync(token);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal((0, 0), (status.Ahead, status.Behind));

        // Branches: create and switch, list local and remote, switch back.
        await repository.CreateBranchAsync("feature/login", token);
        Assert.Equal("feature/login", (await repository.StatusAsync(token)).Branch);
        var branches = await repository.ListBranchesAsync(token);
        Assert.Equal(["feature/login", "main", "origin/main"], branches.Select(b => b.Name));
        Assert.True(branches[0].IsCurrent);
        Assert.Equal("first", branches[1].LastCommitSubject);
        Assert.Equal("origin/main", branches[1].Upstream);
        Assert.True(branches[2].IsRemote);
        await repository.SwitchAsync(branches[1], token);
        Assert.Equal("main", (await repository.StatusAsync(token)).Branch);
        Assert.True(await repository.IsValidBranchNameAsync("fix/thing", token));
        Assert.False(await repository.IsValidBranchNameAsync("bad..name", token));

        // Someone else pushes; we see "behind" after fetching, and pull brings the change in.
        Git("clone", "-q", remote, theirs);
        Configure(theirs);
        await File.WriteAllTextAsync(Path.Combine(theirs, "b.txt"), "theirs\n", token);
        GitIn(theirs, "add", "-A");
        GitIn(theirs, "commit", "-q", "-m", "their change");
        GitIn(theirs, "push", "-q");
        await repository.FetchAsync(token);
        Assert.Equal(1, (await repository.StatusAsync(token)).Behind);
        await repository.PullAsync(token);
        Assert.True(File.Exists(Path.Combine(mine, "b.txt")));
        Assert.Equal(0, (await repository.StatusAsync(token)).Behind);

        // A remote branch can be checked out as a tracking local branch.
        GitIn(theirs, "switch", "-q", "-c", "shared");
        GitIn(theirs, "push", "-q", "-u", "origin", "shared");
        await repository.FetchAsync(token);
        var shared = (await repository.ListBranchesAsync(token)).Single(b => b.Name == "origin/shared");
        await repository.SwitchAsync(shared, token);
        status = await repository.StatusAsync(token);
        Assert.Equal(("shared", "origin/shared"), (status.Branch, status.Upstream));

        // And our own commits go up with a plain push once there is an upstream.
        await File.WriteAllTextAsync(Path.Combine(mine, "c.txt"), "mine\n", token);
        await repository.StageAsync([Path.Combine(mine, "c.txt")], token);
        await repository.CommitAsync("mine", token);
        Assert.Equal(1, (await repository.StatusAsync(token)).Ahead);
        await repository.PushAsync(branch: null, remote: null, token);
        Assert.Equal(0, (await repository.StatusAsync(token)).Ahead);
    }

    [Fact]
    public void Hunk_operations_move_one_change_between_versions()
    {
        string[] staged = ["a", "b", "c", "d"];
        string[] current = ["a", "B", "c", "d", "e"];
        var hunks = Coder.Core.Diff.LineDiff.Compute(staged, current); // b→B, then +e

        Assert.Equal(["a", "B", "c", "d"], Coder.Core.Diff.HunkOperations.Stage(staged, current, hunks[0]));
        Assert.Equal(["a", "b", "c", "d", "e"], Coder.Core.Diff.HunkOperations.Revert(staged, current, hunks[0]));
        Assert.Equal(["a", "b", "c", "d", "e"], Coder.Core.Diff.HunkOperations.Stage(staged, current, hunks[1]));
    }

    [Fact]
    public async Task Staging_content_and_stashing_round_trip()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        Git("init", "-q", "-b", "main");
        Configure(_root);
        var file = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\n", token);
        Git("add", "-A");
        Git("commit", "-q", "-m", "first");
        var repository = GitRepository.Open(_root)!;

        // Stage content that differs from the working tree: only that content goes into the index.
        await File.WriteAllTextAsync(file, "ONE\ntwo\nthree\n", token);
        await repository.StageContentAsync(file, "ONE\ntwo\n", token);
        Assert.Equal("ONE\ntwo\n", (await repository.ShowFileAsync(file, string.Empty, token))!.ReplaceLineEndings("\n"));
        var status = await repository.StatusAsync(token);
        Assert.Single(status.Staged);
        Assert.Single(status.Unstaged); // "three" is still only in the working tree

        // Stash everything, see it listed, pop it back.
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "untracked", token);
        await repository.StashAsync("work in progress", token);
        Assert.Equal(0, (await repository.StatusAsync(token)).ChangeCount);
        var stash = Assert.Single(await repository.ListStashesAsync(token));
        Assert.Equal("stash@{0}", stash.Reference);
        Assert.Contains("work in progress", stash.Description, StringComparison.Ordinal);

        await repository.ApplyStashAsync(stash, drop: true, token);
        Assert.Empty(await repository.ListStashesAsync(token));
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Equal("ONE\ntwo\nthree\n", (await File.ReadAllTextAsync(file, token)).ReplaceLineEndings("\n"));
    }

    private void Configure(string repository)
    {
        GitIn(repository, "config", "user.email", "test@example.com");
        GitIn(repository, "config", "user.name", "Test");
    }

    private static void GitIn(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    [Fact]
    public async Task Git_errors_carry_gits_message()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        Git("init", "-q");
        Git("config", "user.name", "Test"); // CI machines have no identity; git would complain about that first
        Git("config", "user.email", "test@example.com");
        var repository = GitRepository.Open(_root)!;

        var error = await Assert.ThrowsAsync<GitException>(() => repository.CommitAsync("nothing staged", TestContext.Current.CancellationToken));
        Assert.Contains("nothing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
