using Coder.Core.Diff;

namespace Coder.Core.Tests;

public sealed class LineDiffTests
{
    [Fact]
    public void Identical_text_has_no_hunks() =>
        Assert.Empty(LineDiff.Compute(["a", "b"], ["a", "b"]));

    [Fact]
    public void Finds_insertions_deletions_and_modifications()
    {
        string[] before = ["using System;", "class A", "{", "    int x;", "    int y;", "}", ""];
        string[] after = ["using System;", "using System.IO;", "class A", "{", "    long x;", "}", ""];

        var hunks = LineDiff.Compute(before, after);

        Assert.Equal(
            [new DiffHunk(1, 0, 1, 1), new DiffHunk(3, 2, 4, 1)],
            hunks);
        Assert.Equal([DiffHunkKind.Added, DiffHunkKind.Modified], hunks.Select(h => h.Kind));
    }

    [Fact]
    public void Pure_deletion_at_the_end()
    {
        var hunk = Assert.Single(LineDiff.Compute(["a", "b", "c"], ["a"]));
        Assert.Equal(new DiffHunk(1, 2, 1, 0), hunk);
        Assert.Equal(DiffHunkKind.Deleted, hunk.Kind);
    }

    [Fact]
    public void Matches_the_minimal_edit_on_repeated_lines()
    {
        // Moving one "}" must not be reported as rewriting the block.
        var hunks = LineDiff.Compute(["{", "a", "}", "{", "b", "}"], ["{", "a", "}", "{", "c", "}"]);
        Assert.Equal([new DiffHunk(4, 1, 4, 1)], hunks);
    }

    [Fact]
    public void Applying_the_hunks_rebuilds_the_new_text()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var before = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "line" + random.Next(6)).ToList();
            var after = new List<string>(before);
            for (var edit = random.Next(1, 6); edit > 0; edit--)
            {
                var at = random.Next(after.Count + 1);
                if (random.Next(2) == 0 && at < after.Count)
                {
                    after.RemoveAt(at);
                }
                else
                {
                    after.Insert(at, "new" + random.Next(4));
                }
            }

            var hunks = LineDiff.Compute(before, after);

            // Unchanged lines between hunks must match, and the hunks must rebuild "after" from "before".
            var rebuilt = new List<string>();
            var oldIndex = 0;
            foreach (var hunk in hunks)
            {
                rebuilt.AddRange(before.Skip(oldIndex).Take(hunk.OldStart - oldIndex));
                rebuilt.AddRange(after.Skip(hunk.NewStart).Take(hunk.NewCount));
                oldIndex = hunk.OldStart + hunk.OldCount;
            }

            rebuilt.AddRange(before.Skip(oldIndex));
            Assert.Equal(after, rebuilt);
        }
    }
}
