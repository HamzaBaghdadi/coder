using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// The folded (collapsed) regions of one document, a region keeps its first line visible and
/// hides the lines after it up to its end. Maps document lines to visible rows and back, and follows edits:
/// regions after an edit move with it; an edit that touches a region's hidden lines unfolds it.
/// </summary>
public sealed class FoldingState
{
    private readonly SortedDictionary<int, int> _collapsed = []; // first line -> last hidden line
    private List<(int First, int Last)> _hidden = [];            // merged runs of hidden lines

    /// <summary>Folds were added, removed or moved.</summary>
    public event EventHandler? Changed;

    /// <summary>The collapsed regions, by first line.</summary>
    public IReadOnlyList<ScopeRange> Collapsed => [.. _collapsed.Select(c => new ScopeRange(c.Key, c.Value))];

    public bool HasFolds => _collapsed.Count > 0;

    public bool IsCollapsed(int startLine) => _collapsed.ContainsKey(startLine);

    /// <summary>Folds a region (hides lines after <see cref="ScopeRange.StartLine"/> through <see cref="ScopeRange.EndLine"/>).</summary>
    public void Collapse(ScopeRange region)
    {
        if (region.EndLine <= region.StartLine || _collapsed.TryGetValue(region.StartLine, out var end) && end == region.EndLine)
        {
            return;
        }

        _collapsed[region.StartLine] = region.EndLine;
        Rebuild();
    }

    public void Expand(int startLine)
    {
        if (_collapsed.Remove(startLine))
        {
            Rebuild();
        }
    }

    /// <summary>Folds the region when it's open, unfolds it when it's folded.</summary>
    public void Toggle(ScopeRange region)
    {
        if (_collapsed.ContainsKey(region.StartLine))
        {
            Expand(region.StartLine);
        }
        else
        {
            Collapse(region);
        }
    }

    public void CollapseAll(IEnumerable<ScopeRange> regions)
    {
        var any = false;
        foreach (var region in regions.Where(r => r.EndLine > r.StartLine))
        {
            any |= !_collapsed.TryGetValue(region.StartLine, out var end) || end != region.EndLine;
            _collapsed[region.StartLine] = region.EndLine;
        }

        if (any)
        {
            Rebuild();
        }
    }

    public void ExpandAll()
    {
        if (_collapsed.Count > 0)
        {
            _collapsed.Clear();
            Rebuild();
        }
    }

    /// <summary>Unfolds every region that hides <paramref name="line"/> (the caret went there: a search, Go to Definition).</summary>
    public bool Reveal(int line)
    {
        var hiding = _collapsed.Where(c => c.Key < line && line <= c.Value).Select(c => c.Key).ToList();
        foreach (var start in hiding)
        {
            _collapsed.Remove(start);
        }

        if (hiding.Count > 0)
        {
            Rebuild();
        }

        return hiding.Count > 0;
    }

    /// <summary>Whether <paramref name="line"/> is inside a folded region (not its first line).</summary>
    public bool IsHidden(int line)
    {
        foreach (var (first, last) in _hidden)
        {
            if (line < first)
            {
                return false;
            }

            if (line <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The visible row of a line: its line number minus the hidden lines above it. A hidden line maps to its fold's first line's row.</summary>
    public int RowOf(int line)
    {
        var row = line;
        foreach (var (first, last) in _hidden)
        {
            if (line < first)
            {
                break;
            }

            if (line <= last)
            {
                return first - 1 - (line - row); // the visible line before the run
            }

            row -= last - first + 1;
        }

        return row;
    }

    /// <summary>The line shown on a visible row (rows past the end continue past the last line).</summary>
    public int LineAtRow(int row)
    {
        var line = row;
        foreach (var (first, last) in _hidden)
        {
            if (line < first)
            {
                break;
            }

            line += last - first + 1;
        }

        return line;
    }

    /// <summary>How many rows a document of <paramref name="lineCount"/> lines shows.</summary>
    public int VisibleLineCount(int lineCount) => lineCount - _hidden.Sum(h => Math.Max(0, Math.Min(h.Last, lineCount - 1) - h.First + 1));

    /// <summary>The next visible line after <paramref name="line"/> going down (+1) or up (-1), skipping folds.</summary>
    public int Step(int line, int direction)
    {
        var next = line + direction;
        while (IsHidden(next))
        {
            next += direction;
        }

        return direction < 0 && IsHidden(line) ? RowStart(line) : next;
    }

    private int RowStart(int line) => LineAtRow(RowOf(line));

    /// <summary>Follows an edit: folds after it move; one whose hidden lines it touches (or that grows at its first line) opens.</summary>
    public void OnTextChanged(TextChange change)
    {
        if (_collapsed.Count == 0)
        {
            return;
        }

        var first = change.StartLine;
        var last = change.StartLine + change.RemovedLineBreaks;
        var delta = change.InsertedLineBreaks - change.RemovedLineBreaks;
        var moved = new SortedDictionary<int, int>();
        var changed = false;
        foreach (var (start, end) in _collapsed)
        {
            var touchesHidden = first <= end && last >= start + 1;
            var growsAtHeader = first == start && delta != 0;
            if (touchesHidden || growsAtHeader)
            {
                changed = true; // unfolded
            }
            else if (last < start)
            {
                moved[start + delta] = end + delta;
                changed |= delta != 0;
            }
            else
            {
                moved[start] = end;
            }
        }

        if (changed)
        {
            _collapsed.Clear();
            foreach (var (start, end) in moved)
            {
                _collapsed[start] = end;
            }

            Rebuild();
        }
    }

    private void Rebuild()
    {
        // Merge the hidden runs (nested or overlapping folds hide one run).
        var runs = new List<(int First, int Last)>();
        foreach (var (start, end) in _collapsed)
        {
            var (first, last) = (start + 1, end);
            if (runs.Count > 0 && first <= runs[^1].Last + 1)
            {
                runs[^1] = (runs[^1].First, Math.Max(runs[^1].Last, last));
            }
            else
            {
                runs.Add((first, last));
            }
        }

        _hidden = runs;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
