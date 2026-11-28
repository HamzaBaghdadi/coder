using Avalonia;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Diff;

namespace Coder.App.Editor;

/// <summary>
/// Change markers in the gutter, between the line numbers and the text: a green bar for added lines,
/// a blue bar for modified lines, and a red wedge where lines were deleted.
/// </summary>
public sealed partial class EditorView
{
    private const double ChangeBarWidth = 3;

    private static readonly IBrush AddedBar = Palette.ChangeAdded;
    private static readonly IBrush ModifiedBar = Palette.ChangeModified;
    private static readonly IBrush DeletedWedge = Palette.ChangeDeleted;

    private Func<IReadOnlyList<DiffHunk>> _lineChanges = () => [];

    /// <summary>Where the shown document's changed lines come from (read on every repaint).</summary>
    public void SetLineChangesSource(Func<IReadOnlyList<DiffHunk>> lineChanges)
    {
        _lineChanges = lineChanges;
        InvalidateVisual();
    }

    /// <summary>A change marker in the gutter was clicked: the owner shows that change (stage / revert).</summary>
    public event EventHandler<DiffHunk>? ChangeMarkerClicked;

    private double ChangeMarkerX => GutterWidth - FoldColumnWidth - GutterPaddingRight + 8;

    /// <summary>For tests: the middle of a line's change marker, in view coordinates.</summary>
    internal Point ChangeMarkerCenter(int line) => new(ChangeMarkerX + ChangeBarWidth / 2, LineTop(line) + _metrics.LineHeight / 2);

    /// <summary>
    /// When <paramref name="point"/> is on a change marker, reports that change and returns true.
    /// The hit area is wider than the thin bar, so it is easy to click.
    /// </summary>
    private bool TryClickChangeMarker(Point point)
    {
        var x = ChangeMarkerX;
        if (point.X < x - 4 || point.X > x + ChangeBarWidth + 5)
        {
            return false;
        }

        var lineHeight = _metrics.LineHeight;
        var line = LineAtY(point.Y);
        var nearBoundary = LineAtRow((int)Math.Round((point.Y + _offset.Y) / lineHeight)); // deleted-line wedges sit between lines
        foreach (var hunk in _lineChanges())
        {
            var hit = hunk.Kind == DiffHunkKind.Deleted
                ? nearBoundary == hunk.NewStart
                : line >= hunk.NewStart && line < hunk.NewStart + hunk.NewCount;
            if (hit)
            {
                ChangeMarkerClicked?.Invoke(this, hunk);
                return true;
            }
        }

        return false;
    }

    private void DrawLineChanges(DrawingContext context, int firstLine, int lastLine)
    {
        var x = ChangeMarkerX;
        var lineHeight = _metrics.LineHeight;
        foreach (var hunk in _lineChanges())
        {
            if (hunk.Kind == DiffHunkKind.Deleted)
            {
                // A small wedge on the boundary where the lines used to be.
                if (hunk.NewStart < firstLine || hunk.NewStart > lastLine + 1)
                {
                    continue;
                }

                var y = LineTop(hunk.NewStart);
                var wedge = new StreamGeometry();
                using (var g = wedge.Open())
                {
                    g.BeginFigure(new Point(x - 1, y - 4), isFilled: true);
                    g.LineTo(new Point(x + 5, y));
                    g.LineTo(new Point(x - 1, y + 4));
                    g.EndFigure(isClosed: true);
                }

                context.DrawGeometry(DeletedWedge, null, wedge);
                continue;
            }

            var start = Math.Max(firstLine, hunk.NewStart);
            var end = Math.Min(lastLine, hunk.NewStart + hunk.NewCount - 1);
            if (start > end)
            {
                continue;
            }

            var brush = hunk.Kind == DiffHunkKind.Added ? AddedBar : ModifiedBar;
            context.FillRectangle(brush, new Rect(x, LineTop(start), ChangeBarWidth, LineTop(end) + lineHeight - LineTop(start))); // folded lines take no height
        }
    }
}
