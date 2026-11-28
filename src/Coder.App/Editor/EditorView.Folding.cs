using Avalonia;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Editing;

namespace Coder.App.Editor;

/// <summary>
/// Folding: a chevron in the gutter beside every block that can fold (shown while the mouse is over the
/// gutter; a folded block's chevron always shows), a "⋯" after a folded block's first line, and the mapping
/// between document lines and the rows on screen, which skips the folded lines.
/// </summary>
public sealed partial class EditorView
{
    private const double FoldColumnWidth = 16;
    private const double ChevronSize = 4;

    private FoldingState? _folding;
    private Func<IReadOnlyList<ScopeRange>> _foldRanges = () => [];
    private Action<ScopeRange> _toggleFold = _ => { };
    private bool _gutterHovered;
    private int _hoveredFoldLine = -1;
    private (double FontSize, Avalonia.Media.TextFormatting.TextLayout Layout)? _ellipsis;

    /// <summary>
    /// The folds of the shown document (the tab keeps them), the blocks it can fold, and what a chevron click
    /// does (the tab folds or unfolds, moving a caret out of a block being folded). Null folds: no folding.
    /// </summary>
    public void SetFolding(FoldingState? folding, Func<IReadOnlyList<ScopeRange>>? ranges = null, Action<ScopeRange>? toggle = null)
    {
        if (_folding is not null)
        {
            _folding.Changed -= OnFoldsChanged;
        }

        _folding = folding;
        _foldRanges = ranges ?? (() => []);
        _toggleFold = toggle ?? (_ => { });
        if (folding is not null)
        {
            folding.Changed += OnFoldsChanged;
        }

        OnFoldsChanged(this, EventArgs.Empty);
    }

    private void OnFoldsChanged(object? sender, EventArgs e)
    {
        _offset = ClampOffset(_offset);
        RaiseScrollInvalidated(EventArgs.Empty);
        InvalidateMinimap();
        InvalidateVisual();
    }

    // Lines and rows ---------------------------------------------------------------------------------

    private int LineCount => _editor?.Document.LineCount ?? 1;

    private int RowOf(int line) => _folding?.RowOf(line) ?? line;

    private int LineAtRow(int row) => Math.Clamp(_folding?.LineAtRow(row) ?? row, 0, LineCount - 1);

    private bool IsHidden(int line) => _folding?.IsHidden(line) == true;

    /// <summary>How many rows the document shows (its lines minus the folded ones).</summary>
    private int VisibleLineCount => _folding?.VisibleLineCount(LineCount) ?? LineCount;

    /// <summary>The next line shown after <paramref name="line"/> (skipping a folded block in one step).</summary>
    private int NextLine(int line) => _folding is { HasFolds: true } folding ? folding.Step(line, 1) : line + 1;

    /// <summary><paramref name="line"/>, or the first line shown after it when it's folded away.</summary>
    private int ShownFrom(int line) => IsHidden(line) ? NextLine(line) : line;

    /// <summary>The line shown at a y in view coordinates.</summary>
    private int LineAtY(double y) => LineAtRow((int)Math.Floor(Math.Max(0, y + _offset.Y) / _metrics.LineHeight));

    /// <summary>The blocks that can fold (the tab computes them again after edits).</summary>
    internal IReadOnlyList<ScopeRange> FoldRanges() => _editor is null ? [] : _foldRanges();

    /// <summary>The block that folds from <paramref name="line"/>, if any.</summary>
    private ScopeRange? FoldRangeAt(int line)
    {
        var ranges = FoldRanges();
        int low = 0, high = ranges.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (ranges[mid].StartLine == line)
            {
                return ranges[mid];
            }

            if (ranges[mid].StartLine < line)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return null;
    }

    // For tests: where things are, in view coordinates.
    internal Point FoldChevronCenter(int line) => new(FoldColumnLeft + FoldColumnWidth / 2, LineTop(line) + _metrics.LineHeight / 2);

    internal Point FoldedMarkerCenter(int line) => FoldedMarkerBounds(line).Center;

    internal int LineShownAt(double y) => LineAtY(y);

    internal bool IsGutterHovered => _gutterHovered;

    // Drawing ----------------------------------------------------------------------------------------

    private double FoldColumnLeft => GutterWidth - FoldColumnWidth;

    /// <summary>Chevrons: down (open) while the mouse is over the gutter, right (folded) always.</summary>
    private void DrawFoldControls(DrawingContext context, int firstLine, int lastLine)
    {
        var centerX = FoldColumnLeft + FoldColumnWidth / 2 - 2;
        for (var line = firstLine; line <= lastLine; line = NextLine(line))
        {
            var folded = _folding?.IsCollapsed(line) == true;
            if (!folded && (!_gutterHovered || FoldRangeAt(line) is null))
            {
                continue;
            }

            var centerY = LineTop(line) + _metrics.LineHeight / 2;
            var brush = folded || line == _hoveredFoldLine ? Palette.TextPrimary : Palette.TextMuted;
            var pen = new Pen(brush, 1.4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var shape = geometry.Open())
            {
                if (folded)
                {
                    shape.BeginFigure(new Point(centerX - ChevronSize / 2, centerY - ChevronSize), false);
                    shape.LineTo(new Point(centerX + ChevronSize / 2, centerY));
                    shape.LineTo(new Point(centerX - ChevronSize / 2, centerY + ChevronSize));
                }
                else
                {
                    shape.BeginFigure(new Point(centerX - ChevronSize, centerY - ChevronSize / 2), false);
                    shape.LineTo(new Point(centerX, centerY + ChevronSize / 2));
                    shape.LineTo(new Point(centerX + ChevronSize, centerY - ChevronSize / 2));
                }

                shape.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>A "⋯" pill after a folded block's first line (clicking it unfolds the block).</summary>
    private void DrawFoldedMarkers(DrawingContext context, int firstLine, int lastLine)
    {
        if (_folding is not { HasFolds: true } folding)
        {
            return;
        }

        for (var line = firstLine; line <= lastLine; line = NextLine(line))
        {
            if (folding.IsCollapsed(line))
            {
                var bounds = FoldedMarkerBounds(line);
                context.FillRectangle(Palette.InlayHintBackground, bounds, 3);
                var layout = EllipsisLayout();
                layout.Draw(context, new Point(bounds.X + (bounds.Width - layout.Width) / 2, bounds.Y + (bounds.Height - layout.Height) / 2));
            }
        }
    }

    private Rect FoldedMarkerBounds(int line)
    {
        var width = EllipsisLayout().Width + _metrics.CharWidth;
        return new Rect(LineEndX(line) + _metrics.CharWidth / 2, LineTop(line) + 2, width, _metrics.LineHeight - 4);
    }

    /// <summary>Where a line's own content ends, including the "⋯" of a folded block (decorations go after it).</summary>
    private double LineTailX(int line) =>
        _folding?.IsCollapsed(line) == true ? FoldedMarkerBounds(line).Right : LineEndX(line);

    private Avalonia.Media.TextFormatting.TextLayout EllipsisLayout()
    {
        if (_ellipsis is not { } cached || cached.FontSize != _metrics.FontSize)
        {
            cached = (_metrics.FontSize, new Avalonia.Media.TextFormatting.TextLayout("⋯", _metrics.Typeface, _metrics.FontSize, Palette.InlayHint));
            _ellipsis = cached;
        }

        return cached.Layout;
    }

    // Input ------------------------------------------------------------------------------------------

    /// <summary>A click on a chevron folds or unfolds its block; on a "⋯", unfolds it. True when handled.</summary>
    private bool TryClickFold(Point point)
    {
        if (_editor is null || _folding is null)
        {
            return false;
        }

        var line = LineAtY(point.Y);
        if (point.X >= FoldColumnLeft && point.X < GutterWidth && FoldRangeAt(line) is { } range)
        {
            _toggleFold(range);
            return true;
        }

        if (_folding.IsCollapsed(line) && FoldedMarkerBounds(line).Contains(point))
        {
            _folding.Expand(line);
            return true;
        }

        return false;
    }

    /// <summary>The mouse over the gutter shows every chevron; the one under it is highlighted.</summary>
    private void TrackFoldHover(Point point)
    {
        var inGutter = point.X >= 0 && point.X < GutterWidth;
        var line = inGutter && point.X >= FoldColumnLeft ? LineAtY(point.Y) : -1;
        if (inGutter != _gutterHovered || line != _hoveredFoldLine)
        {
            _gutterHovered = inGutter;
            _hoveredFoldLine = line;
            InvalidateVisual();
        }
    }

    private void EndFoldHover()
    {
        if (_gutterHovered)
        {
            _gutterHovered = false;
            _hoveredFoldLine = -1;
            InvalidateVisual();
        }
    }
}
