using Avalonia;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Git;

namespace Coder.App.Editor;

/// <summary>
/// Merge conflict blocks, tinted across the whole width like VS Code: the current side (with its
/// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> marker) green, the incoming side (with <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>) blue, and a common ancestor (diff3) grey.
/// The buttons to resolve them sit in <see cref="ConflictActions"/>, over the marker lines.
/// </summary>
public sealed partial class EditorView
{
    private Func<IReadOnlyList<MergeConflict>> _conflicts = () => [];

    /// <summary>Where the shown document's conflict blocks come from (read on every repaint).</summary>
    public void SetConflictsSource(Func<IReadOnlyList<MergeConflict>> conflicts)
    {
        _conflicts = conflicts;
        InvalidateVisual();
    }

    /// <summary>For overlays: the top of a line in view coordinates, and the line height.</summary>
    internal (double Top, double Height) LineBand(int line) => (LineTop(line), _metrics.LineHeight);

    private void DrawConflicts(DrawingContext context, int firstLine, int lastLine, double width)
    {
        foreach (var conflict in _conflicts())
        {
            if (conflict.End < firstLine || conflict.Start > lastLine)
            {
                continue;
            }

            Band(conflict.Start, conflict.Start, Palette.ConflictCurrentHeader);
            Band(conflict.Current.First, conflict.Current.Last, Palette.ConflictCurrent);
            if (conflict.Base >= 0)
            {
                Band(conflict.Base, conflict.Separator - 1, Palette.ConflictAncestor);
            }

            Band(conflict.Incoming.First, conflict.Incoming.Last, Palette.ConflictIncoming);
            Band(conflict.End, conflict.End, Palette.ConflictIncomingHeader);
        }

        void Band(int first, int last, IBrush brush)
        {
            first = Math.Max(first, firstLine);
            last = Math.Min(last, lastLine);
            if (first <= last)
            {
                context.FillRectangle(brush, new Rect(0, LineTop(first), width, (last - first + 1) * _metrics.LineHeight));
            }
        }
    }
}
