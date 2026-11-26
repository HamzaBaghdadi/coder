namespace Coder.Core.Diff;

/// <summary>
/// A run of changed lines: <see cref="OldCount"/> lines at <see cref="OldStart"/> became
/// <see cref="NewCount"/> lines at <see cref="NewStart"/> (zero-based). One side is empty for pure
/// insertions and deletions.
/// </summary>
public readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount)
{
    public DiffHunkKind Kind => OldCount == 0 ? DiffHunkKind.Added : NewCount == 0 ? DiffHunkKind.Deleted : DiffHunkKind.Modified;
}

public enum DiffHunkKind
{
    Added,
    Deleted,
    Modified,
}

/// <summary>
/// Line diff with the Myers algorithm (the one git uses), after trimming the common start and end,
/// which is all most edits touch. Lines are compared through interned ids, so each comparison is one
/// integer check. When the files differ too much for an exact diff to be worth it, the differing
/// middle is reported as one replacement.
/// </summary>
public static class LineDiff
{
    /// <summary>Limit on the edit distance explored; beyond it the result is one coarse hunk.</summary>
    private const int MaxEditDistance = 4_000;

    public static IReadOnlyList<DiffHunk> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line) => ids.TryGetValue(line, out var id) ? id : ids[line] = ids.Count;
        var a = oldLines.Select(Id).ToArray();
        var b = newLines.Select(Id).ToArray();

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        var oldMiddle = a.AsSpan(prefix, a.Length - prefix - suffix).ToArray();
        var newMiddle = b.AsSpan(prefix, b.Length - prefix - suffix).ToArray();
        if (oldMiddle.Length == 0 && newMiddle.Length == 0)
        {
            return [];
        }

        var hunks = Myers(oldMiddle, newMiddle) ?? [new DiffHunk(0, oldMiddle.Length, 0, newMiddle.Length)];
        return hunks.Select(h => h with { OldStart = h.OldStart + prefix, NewStart = h.NewStart + prefix }).ToList();
    }

    /// <summary>Splits text into lines the way the editor does (on <c>\n</c>, ignoring <c>\r</c>).</summary>
    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>
    /// Greedy forward Myers: finds the furthest point on each diagonal for growing edit distances, keeps
    /// one snapshot per distance, then walks back to collect the edit script. Null when too different.
    /// </summary>
    private static List<DiffHunk>? Myers(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length, max = Math.Min(n + m, MaxEditDistance);
        var offset = max + 1;
        var v = new int[2 * offset + 1];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]          // step down: an insertion
                    : v[offset + k - 1] + 1;     // step right: a deletion
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, offset, n, m);
                }
            }
        }

        return null;
    }

    private static List<DiffHunk> Backtrack(List<int[]> trace, int offset, int n, int m)
    {
        // Walk back from the end, recording each deleted (old) and inserted (new) line.
        var deleted = new bool[n];
        var inserted = new bool[m];
        int x = n, y = m;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var previousK = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? k + 1 : k - 1;
            var previousX = v[offset + previousK];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                x--;
                y--;
            }

            if (x == previousX)
            {
                inserted[--y] = true;
            }
            else
            {
                deleted[--x] = true;
            }
        }

        // Group consecutive changes into hunks by walking both sides in step.
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        while (i < n || j < m)
        {
            if (i < n && j < m && !deleted[i] && !inserted[j])
            {
                i++;
                j++;
                continue;
            }

            int oldStart = i, newStart = j;
            while (i < n && deleted[i])
            {
                i++;
            }

            while (j < m && inserted[j])
            {
                j++;
            }

            hunks.Add(new DiffHunk(oldStart, i - oldStart, newStart, j - newStart));
        }

        return hunks;
    }
}

/// <summary>Moving one change between two versions of a file (stage it, or revert it).</summary>
public static class HunkOperations
{
    /// <summary>The staged lines with one change applied: what staging just that change makes the index.</summary>
    public static IReadOnlyList<string> Stage(IReadOnlyList<string> staged, IReadOnlyList<string> current, DiffHunk hunk) =>
        Replace(staged, hunk.OldStart, hunk.OldCount, current, hunk.NewStart, hunk.NewCount);

    /// <summary>The current lines with one change undone: the change's lines go back to their staged text.</summary>
    public static IReadOnlyList<string> Revert(IReadOnlyList<string> staged, IReadOnlyList<string> current, DiffHunk hunk) =>
        Replace(current, hunk.NewStart, hunk.NewCount, staged, hunk.OldStart, hunk.OldCount);

    private static List<string> Replace(IReadOnlyList<string> target, int start, int count, IReadOnlyList<string> source, int sourceStart, int sourceCount)
    {
        var result = new List<string>(target.Count - count + sourceCount);
        result.AddRange(target.Take(start));
        result.AddRange(source.Skip(sourceStart).Take(sourceCount));
        result.AddRange(target.Skip(start + count));
        return result;
    }
}
