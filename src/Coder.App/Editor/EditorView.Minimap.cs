using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Syntax;

namespace Coder.App.Editor;

/// <summary>
/// The minimap, like VS Code's: the code in miniature on the right edge (one pixel per character, two
/// per line), with a slider showing the visible part. Click or drag it to scroll. For long files it
/// scrolls along proportionally. Shapes are built once per change (one geometry per token color), so
/// repaints such as the caret blinking cost only a few draw calls.
/// </summary>
public sealed partial class EditorView
{
    internal const double MinimapWidth = 96;
    private const double MinimapLineHeight = 2;
    private const double MinimapCharWidth = 1;
    private const double MinimapPadding = 6;

    private readonly List<Token> _minimapTokens = [];
    private (int Start, int Rows, object? Theme)? _minimapChunk; // the band of lines the cached shapes cover
    private (StreamGeometry Geometry, TokenKind Kind)[] _minimapShapes = [];
    private double? _minimapGrab; // slider drag: where on the slider it was grabbed
    private bool _isMinimapHovered;
    private bool _showMinimap = true;
    private bool _minimapRenderCharacters;

    /// <summary>Whether the minimap is shown ("View: Toggle Minimap").</summary>
    public bool ShowMinimap
    {
        get => _showMinimap;
        set
        {
            if (_showMinimap != value)
            {
                _showMinimap = value;
                RaiseScrollInvalidated(EventArgs.Empty); // the extent includes the minimap
                InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// Off (the default): each line is a few solid blocks in its token colors, from its indent to its end.
    /// On: every word is its own mark, closer to the code's shape.
    /// </summary>
    public bool MinimapRenderCharacters
    {
        get => _minimapRenderCharacters;
        set
        {
            if (_minimapRenderCharacters != value)
            {
                _minimapRenderCharacters = value;
                InvalidateMinimap();
                InvalidateVisual();
            }
        }
    }

    /// <summary>Room the minimap takes on the right (long lines can scroll out from under it).</summary>
    private double MinimapReserve => _showMinimap && _editor is not null && Bounds.Width > MinimapWidth * 4 ? MinimapWidth : 0;

    private double MinimapLeft => Bounds.Width - MinimapWidth;

    private bool IsInMinimap(Point point) => MinimapReserve > 0 && point.X >= MinimapLeft;

    private void InvalidateMinimap() => _minimapChunk = null;

    /// <summary>Rows that fit, the first line shown, and where the slider is.</summary>
    private (int Rows, int First, double SliderTop, double SliderHeight) MinimapLayout()
    {
        var document = _editor!.Document;
        var rows = Math.Max(1, (int)(Bounds.Height / MinimapLineHeight));
        var lineHeight = _metrics.LineHeight;
        // The minimap shows every line (folded ones too): the scroll position, in rows, becomes lines.
        var topRow = _offset.Y / lineHeight;
        var firstVisible = LineAtRow((int)topRow) + (topRow - Math.Floor(topRow));
        var visibleLines = Math.Max(1, LineAtRow((int)(topRow + _viewport.Height / lineHeight)) - firstVisible);
        var ratio = Math.Clamp(firstVisible / Math.Max(1, document.LineCount - 1), 0, 1);
        var first = document.LineCount > rows ? (int)Math.Round(ratio * (document.LineCount - rows)) : 0;
        return (rows, first, (firstVisible - first) * MinimapLineHeight, visibleLines * MinimapLineHeight);
    }

    private void DrawMinimap(DrawingContext context)
    {
        if (MinimapReserve == 0)
        {
            return;
        }

        var (rows, first, sliderTop, sliderHeight) = MinimapLayout();
        var area = new Rect(MinimapLeft, 0, MinimapWidth, Bounds.Height);
        context.FillRectangle(Palette.EditorBackground, area);
        context.FillRectangle(Palette.Border, new Rect(area.X, 0, 1, area.Height)); // a hairline between code and map

        // Shapes cover a band of about three screens around what is shown, so scrolling only moves them;
        // they are rebuilt when the view leaves the band, the text changes, or the theme does.
        if (_minimapChunk is not { } chunk || first < chunk.Start || first + rows > chunk.Start + chunk.Rows || !ReferenceEquals(chunk.Theme, Palette.Current))
        {
            chunk = (Math.Max(0, first - rows), rows * 3, Palette.Current);
            _minimapShapes = BuildMinimap(chunk.Start, chunk.Rows);
            _minimapChunk = chunk;
        }

        using (context.PushClip(area))
        {
            using (context.PushTransform(Matrix.CreateTranslation(area.X + MinimapPadding, (chunk.Start - first) * MinimapLineHeight)))
            using (context.PushOpacity(0.7))
            {
                foreach (var (geometry, kind) in _minimapShapes)
                {
                    context.DrawGeometry(SyntaxColors.For(kind), null, geometry);
                }
            }

            var slider = new Rect(area.X, sliderTop, MinimapWidth, Math.Max(sliderHeight, 8));
            context.FillRectangle(_minimapGrab is not null ? Palette.ScrollbarThumbActive : _isMinimapHovered ? Palette.ScrollbarThumbHover : Palette.ScrollbarThumb, slider);
        }
    }

    /// <summary>The lines in view of the map: one bar per line, or runs of non-blank characters grouped by token color.</summary>
    private (StreamGeometry, TokenKind)[] BuildMinimap(int first, int rows)
    {
        var document = _editor!.Document;
        var last = Math.Min(document.LineCount - 1, first + rows);
        var maxColumns = (int)((MinimapWidth - MinimapPadding * 2) / MinimapCharWidth);
        const double left = 0; // placed at draw time, so resizing the window keeps the shapes
        var tabSize = TabSize;
        var shapes = new Dictionary<TokenKind, (StreamGeometry Geometry, StreamGeometryContext Context)>();

        for (var line = first; line <= last; line++)
        {
            var text = document.GetLineText(line);
            if (text.Length == 0)
            {
                continue;
            }

            _highlighter!.GetTokens(line, _minimapTokens);

            var y = (line - first) * MinimapLineHeight;
            var column = 0;
            var tokenIndex = 0;
            var runStart = -1;
            var runKind = TokenKind.Text;

            void EndRun(int end)
            {
                if (runStart < 0)
                {
                    return;
                }

                if (!shapes.TryGetValue(runKind, out var shape))
                {
                    var geometry = new StreamGeometry();
                    shape = (geometry, geometry.Open());
                    shapes[runKind] = shape;
                }

                var x0 = left + runStart * MinimapCharWidth;
                var x1 = left + Math.Min(end, maxColumns) * MinimapCharWidth;
                shape.Context.BeginFigure(new Point(x0, y), true);
                shape.Context.LineTo(new Point(x1, y));
                shape.Context.LineTo(new Point(x1, y + MinimapLineHeight - 0.5));
                shape.Context.LineTo(new Point(x0, y + MinimapLineHeight - 0.5));
                shape.Context.EndFigure(true);
                runStart = -1;
            }

            for (var i = 0; i < text.Length && column < maxColumns; i++)
            {
                while (tokenIndex < _minimapTokens.Count && _minimapTokens[tokenIndex].Start + _minimapTokens[tokenIndex].Length <= i)
                {
                    tokenIndex++;
                }

                var kind = tokenIndex < _minimapTokens.Count && _minimapTokens[tokenIndex].Start <= i ? _minimapTokens[tokenIndex].Kind : TokenKind.Text;
                var c = text[i];
                var width = c == '\t' ? tabSize - column % tabSize : 1;
                // Characters: every word is its own mark. Blocks: a mark per token color, spaces between words
                // included, so a line reads as a few solid colored pieces.
                var breaks = _minimapRenderCharacters ? char.IsWhiteSpace(c) || kind != runKind : !char.IsWhiteSpace(c) && kind != runKind;
                if (breaks)
                {
                    EndRun(column);
                }

                if (!char.IsWhiteSpace(c) && runStart < 0)
                {
                    runStart = column;
                    runKind = kind;
                }

                column += width;
            }

            if (!_minimapRenderCharacters)
            {
                column = Math.Min(column, ColumnsWithoutTrailingSpace(text, tabSize)); // the bar ends at the last character
            }

            EndRun(column);
        }

        foreach (var (_, context) in shapes.Values)
        {
            context.Dispose();
        }

        return shapes.Select(pair => (pair.Value.Geometry, pair.Key)).ToArray();
    }

    private static int ColumnsWithoutTrailingSpace(string text, int tabSize)
    {
        var end = text.TrimEnd().Length;
        var column = 0;
        for (var i = 0; i < end; i++)
        {
            column += text[i] == '	' ? tabSize - column % tabSize : 1;
        }

        return column;
    }

    // Input -----------------------------------------------------------------------------------------

    /// <summary>A press on the map: on the slider it grabs it; elsewhere it jumps there (centred) and grabs.</summary>
    private bool MinimapPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_editor is null || !IsInMinimap(point.Position) || !point.Properties.IsLeftButtonPressed)
        {
            return false;
        }

        var (_, _, sliderTop, sliderHeight) = MinimapLayout();
        var y = point.Position.Y;
        if (y < sliderTop || y > sliderTop + sliderHeight)
        {
            var (_, first, _, _) = MinimapLayout();
            var line = first + y / MinimapLineHeight;
            Offset = new Vector(_offset.X, (RowOf((int)line) - _viewport.Height / _metrics.LineHeight / 2) * _metrics.LineHeight);
            RaiseScrollInvalidated(EventArgs.Empty); // the scroll bar follows
            (_, _, sliderTop, _) = MinimapLayout();
        }

        _minimapGrab = y - sliderTop;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
        return true;
    }

    /// <summary>While the slider is held, it follows the pointer exactly.</summary>
    private bool MinimapMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var hovered = IsInMinimap(position);
        if (hovered != _isMinimapHovered)
        {
            _isMinimapHovered = hovered;
            InvalidateVisual();
        }

        if (_minimapGrab is not { } grab || _editor is null)
        {
            if (hovered)
            {
                Cursor = Cursor.Default;
            }

            return hovered;
        }

        // sliderTop = 2·ratio·(min(rows, lines) − 1), so the ratio follows from where the slider should be.
        var document = _editor.Document;
        var rows = Math.Max(1, (int)(Bounds.Height / MinimapLineHeight));
        var span = MinimapLineHeight * Math.Max(1, Math.Min(rows, document.LineCount) - 1);
        var ratio = Math.Clamp((position.Y - grab) / span, 0, 1);
        Offset = new Vector(_offset.X, RowOf((int)Math.Round(ratio * (document.LineCount - 1))) * _metrics.LineHeight);
        RaiseScrollInvalidated(EventArgs.Empty); // the scroll bar follows
        e.Handled = true;
        return true;
    }

    private bool MinimapReleased(PointerReleasedEventArgs e)
    {
        if (_minimapGrab is null)
        {
            return false;
        }

        _minimapGrab = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        return true;
    }

    private void MinimapPointerExited()
    {
        if (_isMinimapHovered)
        {
            _isMinimapHovered = false;
            InvalidateVisual();
        }
    }
}
