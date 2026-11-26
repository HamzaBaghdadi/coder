using System.Diagnostics;
using Coder.Core.Git;

namespace Coder.Core.Tests;

/// <summary>History: log, commit files, blame (on real temporary repositories) and the graph layout.</summary>
public sealed class GitHistoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-history-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // Graph -------------------------------------------------------------------------------------------

    [Fact]
    public void A_straight_history_stays_in_one_lane()
    {
        var rows = GitGraph.Layout([Commit("c", "b"), Commit("b", "a"), Commit("a")]);

        Assert.All(rows, row => Assert.Equal(0, row.Lane));
        Assert.Equal([new GraphSegment(0, 0, false, 0)], rows[0].Segments);                                      // tip: only down
        Assert.Equal([new GraphSegment(0, 0, true, 0), new GraphSegment(0, 0, false, 0)], rows[1].Segments);     // through
        Assert.Equal([new GraphSegment(0, 0, true, 0)], rows[2].Segments);                                       // root: only up
    }

    [Fact]
    public void A_merge_opens_a_lane_that_joins_again_at_the_fork()
    {
        // m merges f into b; both come from a.
        //   m        lane 0, opens lane 1 for f
        //   | f      lane 1
        //   b |      lane 0, lane 1 passes
        //   a        lane 0, lane 1 joins it
        var rows = GitGraph.Layout([Commit("m", "b", "f"), Commit("f", "a"), Commit("b", "a"), Commit("a")]);

        Assert.Equal([0, 1, 0, 0], rows.Select(r => r.Lane));
        Assert.Contains(new GraphSegment(0, 1, false, 1), rows[0].Segments); // the merge line to f's lane
        Assert.Contains(new GraphSegment(1, 1, true, 1), rows[2].Segments);  // f's lane passing b
        Assert.Contains(new GraphSegment(1, 0, true, 1), rows[3].Segments);  // f's lane joining a
        Assert.Equal(2, rows[0].Width);
        Assert.DoesNotContain(rows[3].Segments, s => !s.IsTop); // nothing continues below the root
    }

    [Fact]
    public void Two_branch_tips_get_their_own_lanes_and_colors()
    {
        var rows = GitGraph.Layout([Commit("x", "a"), Commit("y", "a"), Commit("a")]);

        Assert.Equal([0, 1, 0], rows.Select(r => r.Lane));
        Assert.NotEqual(rows[0].Color, rows[1].Color);
        Assert.Contains(new GraphSegment(1, 0, true, 1), rows[2].Segments);
    }

    // Parsing -----------------------------------------------------------------------------------------

    [Fact]
    public void Parses_blame_porcelain_with_repeated_commits_and_uncommitted_lines()
    {
        const string Hash = "1111111111111111111111111111111111111111";
        const string Zero = "0000000000000000000000000000000000000000";
        var output =
            $"{Hash} 1 1 2\nauthor Ada\nauthor-mail <ada@example.com>\nauthor-time 1700000000\nauthor-tz +0000\nsummary First\nfilename a.txt\n\tone\n" +
            $"{Hash} 2 2\n\ttwo\n" +
            $"{Zero} 3 3 1\nauthor Not Committed Yet\nauthor-mail <not.committed.yet>\nauthor-time 1700000100\nsummary Version of a.txt\nfilename a.txt\n\tthree\n";

        var lines = GitHistoryParser.ParseBlame(output);

        Assert.Equal(3, lines.Count);
        Assert.Same(lines[0], lines[1]);
        Assert.Equal(("Ada", "ada@example.com", "First", true), (lines[0].Author, lines[0].Email, lines[0].Summary, lines[0].IsCommitted));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), lines[0].Date);
        Assert.False(lines[2].IsCommitted);
    }

    // Real repositories -------------------------------------------------------------------------------

    [Fact]
    public async Task Log_commit_files_and_blame_on_a_real_repository()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "ada@example.com");
        Git("config", "user.name", "Ada");
        Git("config", "core.autocrlf", "false");
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\n", token);
        Git("add", "-A");
        Git("commit", "-q", "-m", "First");
        await File.WriteAllTextAsync(file, "one\nTWO\n", token);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "b\n", token);
        Git("add", "-A");
        Git("commit", "-q", "-m", "Second", "-m", "With a body.");
        Git("tag", "v1");

        var repository = GitRepository.Open(_root)!;
        var log = await repository.LogAsync(50, cancellationToken: token);
        Assert.Equal(["Second", "First"], log.Select(c => c.Subject));
        Assert.Equal("With a body.", log[0].Body);
        Assert.Equal(log[1].Hash, log[0].Parent);
        Assert.Equal(("Ada", "ada@example.com"), (log[0].Author, log[0].Email));
        Assert.Contains("HEAD -> main", log[0].Refs);
        Assert.Contains("tag: v1", log[0].Refs);
        Assert.Empty(log[1].Parents);

        var second = await repository.CommitChangesAsync(log[0], token);
        Assert.Equal(["a.txt:Modified", "b.txt:Added"], second.Select(c => $"{c.RelativePath}:{c.Kind}"));
        var first = await repository.CommitChangesAsync(log[1], token);
        Assert.Equal(["a.txt:Added"], first.Select(c => $"{c.RelativePath}:{c.Kind}"));

        Assert.Equal(["Second"], (await repository.FileLogAsync(Path.Combine(_root, "b.txt"), 10, token)).Select(c => c.Subject));

        // Blame of the editor's text: an unsaved third line is not committed yet.
        var blame = await repository.BlameAsync(file, "one\nTWO\nthree\n", token);
        Assert.Equal(["First", "Second"], blame.Take(2).Select(l => l.Summary));
        Assert.False(blame[2].IsCommitted);
        Assert.Empty(await repository.BlameAsync(Path.Combine(_root, "untracked.txt"), "x\n", token));

        // A large unsaved file git refuses at once: git stops reading before all the text is sent, which
        // must still end as "no blame", not as a broken-pipe error.
        Assert.Empty(await repository.BlameAsync(Path.Combine(_root, "untracked.txt"), string.Concat(Enumerable.Repeat("a line of text\n", 200_000)), token));
        Assert.Equal("ada@example.com", await repository.UserEmailAsync(token));
    }

    [Fact]
    public async Task An_empty_repository_has_no_history()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        Git("init", "-q");
        Assert.Empty(await GitRepository.Open(_root)!.LogAsync(10, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static GitCommit Commit(string hash, params string[] parents) =>
        new(hash, parents, "A", "a@example.com", DateTimeOffset.UnixEpoch, hash, string.Empty, []);

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
