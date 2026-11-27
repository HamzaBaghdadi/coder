using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// A snippet being filled in: Tab moves to the next placeholder, Shift+Tab back, and stops that share a
/// number (mirrors) are selected together so typing edits them all. The session ends at the final
/// stop, on Escape, on undo, or when the caret leaves the snippet.
/// </summary>
public sealed partial class EditorState
{
    private SnippetSession? _snippet;

    public bool IsInSnippet => _snippet is not null;

    /// <summary>
    /// True while the caret is filling in the current snippet placeholder (where Tab means "next stop"),
    /// rather than somewhere else in or after the snippet.
    /// </summary>
    public bool IsCaretOnSnippetStop
    {
        get
        {
            if (_snippet is not { } session)
            {
                return false;
            }

            var number = session.Order[session.Index];
            var caret = Selection.Active;
            return session.Stops.Any(s => s.Number == number && caret >= s.Start && caret <= s.End);
        }
    }

    /// <summary>
    /// Starts navigating the tab stops of an inserted snippet (absolute offsets). Nothing starts when
    /// there is no stop to move to.
    /// </summary>
    public void StartSnippet(IReadOnlyList<(int Number, int Start, int End)> stops, int start, int end)
    {
        EndSnippet();
        var order = stops.Select(s => s.Number).Where(n => n > 0).Distinct().Order().ToList();
        if (order.Count == 0 || (order.Count == 1 && stops.Count(s => s.Number == order[0]) == 1 && !stops.Any(s => s.Number == 0)))
        {
            return; // a single placeholder: the initial selection is all there is
        }

        order.Add(0); // the final position: an explicit $0, else the snippet's end
        var allStops = stops.Select(s => new Stop(s.Number, s.Start, s.End)).ToList();
        if (!allStops.Any(s => s.Number == 0))
        {
            allStops.Add(new Stop(0, end, end));
        }

        _snippet = new SnippetSession(allStops, order, new Stop(-1, start, end));
        Document.Changed += MapSnippet;
        SelectCurrentStop();
    }

    /// <summary>Tab / Shift+Tab inside a snippet. Returns false when there is no snippet to navigate.</summary>
    public bool MoveToTabStop(bool backwards)
    {
        if (_snippet is not { } session)
        {
            return false;
        }

        var caret = Selection.Active;
        if (caret < session.Bounds.Start || caret > session.Bounds.End)
        {
            EndSnippet(); // the user went elsewhere
            return false;
        }

        session.Index = Math.Clamp(session.Index + (backwards ? -1 : 1), 0, session.Order.Count - 1);
        SelectCurrentStop();
        if (session.Order[session.Index] == 0)
        {
            EndSnippet();
        }

        return true;
    }

    public void EndSnippet()
    {
        if (_snippet is not null)
        {
            Document.Changed -= MapSnippet;
            _snippet = null;
        }
    }

    private void SelectCurrentStop()
    {
        var session = _snippet!;
        var number = session.Order[session.Index];
        var group = session.Stops.Where(s => s.Number == number).Select(s => new Selection(s.Start, s.End)).ToList();
        SetSelections(SelectionSet.Create(group, group[0]));
    }

    /// <summary>Stops follow edits; typing over a placeholder grows its range to the typed text.</summary>
    private void MapSnippet(object? sender, TextChange change)
    {
        var session = _snippet!;
        static Stop Map(Stop stop, TextChange change)
        {
            var start = change.MapOffset(stop.Start);
            var end = change.Offset >= stop.Start && change.Offset <= stop.End
                ? stop.End + change.InsertedText.Length - Math.Min(change.RemovedText.Length, stop.End - change.Offset)
                : change.MapOffset(stop.End);
            return stop with { Start = start, End = Math.Max(start, end) };
        }

        for (var i = 0; i < session.Stops.Count; i++)
        {
            session.Stops[i] = Map(session.Stops[i], change);
        }

        session.Bounds = Map(session.Bounds, change);
    }

    private readonly record struct Stop(int Number, int Start, int End);

    private sealed class SnippetSession(List<Stop> stops, List<int> order, Stop bounds)
    {
        public List<Stop> Stops { get; } = stops;

        /// <summary>Stop numbers in visiting order: 1, 2, … then 0.</summary>
        public List<int> Order { get; } = order;

        public int Index { get; set; }

        public Stop Bounds { get; set; } = bounds;
    }
}
