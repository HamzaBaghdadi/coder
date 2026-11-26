using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// Where text is on screen. Positions come from each line's real layout (<see cref="LineLayout"/>),
/// so carets, selections and clicks stay on the glyphs for wide characters (CJK, emoji), shaped
/// scripts (Arabic) and right-to-left runs. All X values here are in view coordinates.
/// </summary>
public sealed partial class EditorView
{
    /// <summary>The (cached) layout of <paramref name="line"/>; long lines are sliced to the visible columns.</summary>
    private LineLayout LayoutOf(int line)
    {
        var firstColumn = (int)(_offset.X / _metrics.CharWidth);
        var columnCount = (int)(_viewport.Width / _metrics.CharWidth) + 2;
        return _layouts.Get(line, _editor!.Document, _highlighter!, _metrics, TabSize, firstColumn, columnCount, InsertionsOf(line));
    }

    /// <summary>X of the layout's origin.</summary>
    private double LayoutLeft(LineLayout layout) => ColumnX(layout.StartColumn, TextLeft);

    /// <summary>Character column → display index (tabs expanded).</summary>
    private int DisplayIndex(string lineText, int column) => TextColumns.ToVisual(lineText, column, TabSize);

    /// <summary>X of the caret before character <paramref name="column"/> of <paramref name="line"/>.</summary>
    private double CaretX(int line, int column)
    {
        var layout = LayoutOf(line);
        var index = DisplayIndex(_editor!.Document.GetLineText(line), column);
        return layout.Covers(index) ? LayoutLeft(layout) + layout.XOf(index) : ColumnX(index, TextLeft);
    }

    /// <summary>The character column of <paramref name="line"/> nearest to <paramref name="x"/>.</summary>
    internal int ColumnAtX(int line, double x)
    {
        var layout = LayoutOf(line);
        var local = x - LayoutLeft(layout);
        var index = local >= 0 && local <= layout.Width
            ? layout.IndexAt(local)
            : (int)Math.Round((x - TextLeft + _offset.X) / _metrics.CharWidth); // outside a sliced long line
        var lineText = _editor!.Document.GetLineText(line);
        return local > layout.Width && layout.StartColumn == 0
            ? lineText.Length
            : TextColumns.FromVisual(lineText, Math.Max(0, index), TabSize);
    }

    /// <summary>Horizontal spans covering characters <paramref name="fromColumn"/>..<paramref name="toColumn"/> of a line.</summary>
    private IEnumerable<(double Left, double Right)> SpansOf(int line, int fromColumn, int toColumn)
    {
        var layout = LayoutOf(line);
        var lineText = _editor!.Document.GetLineText(line);
        var from = DisplayIndex(lineText, fromColumn);
        var to = DisplayIndex(lineText, toColumn);
        if (layout.Covers(from) && layout.Covers(to))
        {
            var left = LayoutLeft(layout);
            return layout.SpansOf(from, to).Select(span => (left + span.Left, left + span.Right));
        }

        return [(ColumnX(from, TextLeft), ColumnX(to, TextLeft))];
    }

    /// <summary>X just past the last character of a line (where a selected line break is drawn).</summary>
    private double LineEndX(int line)
    {
        var layout = LayoutOf(line);
        return LayoutLeft(layout) + layout.Width;
    }
}
