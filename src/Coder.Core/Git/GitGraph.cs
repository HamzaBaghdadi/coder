namespace Coder.Core.Git;

/// <summary>
/// One line segment of a graph row. A row is split at its middle, where the commit's dot sits: a top
/// segment runs from lane <see cref="From"/> at the row's top edge to lane <see cref="To"/> at the middle,
/// a bottom segment from the middle to the bottom edge. <see cref="Color"/> picks one of a few colors.
/// </summary>
public readonly record struct GraphSegment(int From, int To, bool IsTop, int Color);

/// <summary>How to draw one commit's row: its dot's lane and color, and the lines passing through or joining it.</summary>
public sealed record GraphRow(int Lane, int Color, IReadOnlyList<GraphSegment> Segments)
{
    /// <summary>Lanes in use in this row (the width to reserve).</summary>
    public int Width => Math.Max(Lane, Segments.Count == 0 ? 0 : Segments.Max(s => Math.Max(s.From, s.To))) + 1;
}

/// <summary>
/// Lays out the commit graph like VS Code's and git's: each line of development keeps a lane (and a
/// color) while it continues down the list. A merge commit opens a lane for its other parent; lanes
/// that reach the same commit join at its dot, and the freed lane is reused.
/// </summary>
/// <remarks>The commits must list children before parents (<c>git log --topo-order</c>).</remarks>
public static class GitGraph
{
    public static IReadOnlyList<GraphRow> Layout(IReadOnlyList<GitCommit> commits)
    {
        var lanes = new List<(string? Hash, int Color)>();
        var nextColor = 0;
        var rows = new List<GraphRow>(commits.Count);
        foreach (var commit in commits)
        {
            var segments = new List<GraphSegment>();

            // Its lane: the one waiting for it, or a new one (a branch tip).
            var lane = lanes.FindIndex(l => l.Hash == commit.Hash);
            var isTip = lane < 0;
            if (isTip)
            {
                lane = FreeLane(lanes);
                lanes[lane] = (commit.Hash, nextColor++);
            }

            var color = lanes[lane].Color;

            // Top half: lanes waiting for this commit join its dot; the others pass by.
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].Hash is null || (i == lane && isTip))
                {
                    continue;
                }

                var joins = lanes[i].Hash == commit.Hash;
                segments.Add(new GraphSegment(i, joins ? lane : i, IsTop: true, lanes[i].Color));
                if (joins && i != lane)
                {
                    lanes[i] = (null, 0);
                }
            }

            // Bottom half: its lane continues to the first parent; each other parent gets a lane.
            var opened = new HashSet<int>();
            lanes[lane] = commit.Parent is { } first ? (first, color) : (null, 0);
            foreach (var parent in commit.Parents.Skip(1))
            {
                var target = lanes.FindIndex(l => l.Hash == parent);
                if (target < 0)
                {
                    target = FreeLane(lanes);
                    lanes[target] = (parent, nextColor++);
                    opened.Add(target);
                }

                segments.Add(new GraphSegment(lane, target, IsTop: false, lanes[target].Color));
            }

            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].Hash is not null && !opened.Contains(i))
                {
                    segments.Add(new GraphSegment(i, i, IsTop: false, lanes[i].Color));
                }
            }

            while (lanes.Count > 0 && lanes[^1].Hash is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
            }

            rows.Add(new GraphRow(lane, color, segments));
        }

        return rows;
    }

    private static int FreeLane(List<(string? Hash, int Color)> lanes)
    {
        var free = lanes.FindIndex(l => l.Hash is null);
        if (free >= 0)
        {
            return free;
        }

        lanes.Add((null, 0));
        return lanes.Count - 1;
    }
}
