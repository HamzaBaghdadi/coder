namespace Coder.Core.Text;

/// <summary>
/// All selections (cursors) of an editor: sorted by position, never overlapping, with one marked as
/// primary (the one that scrolls into view and that single-cursor features use). Immutable.
/// </summary>
public sealed class SelectionSet : IEquatable<SelectionSet>
{
    private SelectionSet(IReadOnlyList<Selection> all, int primaryIndex)
    {
        All = all;
        PrimaryIndex = primaryIndex;
    }

    /// <summary>The selections in document order.</summary>
    public IReadOnlyList<Selection> All { get; }

    public int PrimaryIndex { get; }

    public Selection Primary => All[PrimaryIndex];

    public int Count => All.Count;

    public static SelectionSet Single(Selection selection) => new([selection], 0);

    /// <summary>
    /// Builds a normalized set: sorted, with overlapping selections (and cursors at the same spot)
    /// merged. The primary is the merged selection that contains <paramref name="primary"/>.
    /// </summary>
    public static SelectionSet Create(IEnumerable<Selection> selections, Selection primary)
    {
        var sorted = selections.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        if (sorted.Count == 0)
        {
            return Single(primary);
        }

        var merged = new List<Selection> { sorted[0] };
        foreach (var next in sorted.Skip(1))
        {
            var last = merged[^1];
            var overlaps = next.Start < last.End || next.Start == last.Start || (last.IsEmpty && next.Start == last.End)
                           || (next.IsEmpty && next.Start == last.End);
            if (!overlaps)
            {
                merged.Add(next);
                continue;
            }

            // Keep the direction of the selection we started from.
            var start = Math.Min(last.Start, next.Start);
            var end = Math.Max(last.End, next.End);
            merged[^1] = last.Active >= last.Anchor ? new Selection(start, end) : new Selection(end, start);
        }

        var primaryIndex = merged.FindIndex(s => s.Start <= primary.Active && primary.Active <= s.End);
        return new SelectionSet(merged, primaryIndex < 0 ? merged.Count - 1 : primaryIndex);
    }

    public SelectionSet Clamp(int documentLength) =>
        Create(All.Select(s => s.Clamp(documentLength)), Primary.Clamp(documentLength));

    public bool Equals(SelectionSet? other) =>
        other is not null && PrimaryIndex == other.PrimaryIndex && All.SequenceEqual(other.All);

    public override bool Equals(object? obj) => Equals(obj as SelectionSet);

    public override int GetHashCode() => HashCode.Combine(PrimaryIndex, All.Count, Primary);

    public static bool operator ==(SelectionSet? left, SelectionSet? right) => Equals(left, right);

    public static bool operator !=(SelectionSet? left, SelectionSet? right) => !Equals(left, right);
}
