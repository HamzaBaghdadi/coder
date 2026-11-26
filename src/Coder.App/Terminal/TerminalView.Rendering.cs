using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Coder.App.Editor;
using Coder.App.Theme;
using Coder.Core.Terminal;

namespace Coder.App.Terminal;

/// <summary>Painting the cell grid.</summary>
public sealed partial class TerminalView
{
    private static readonly IBrush CursorFill = Palette.TerminalCursor;
    private static readonly Pen CursorOutline = new(Palette.Caret, 1);
    private static readonly Pen CurrentMatchOutline = new(Palette.SearchMatchBorder, 1);

    // Shaped rows, rebuilt only when a line changes (TerminalLine.Version).
    private readonly ConditionalWeakTable<TerminalLine, ShapedRow> _rowCache = [];

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(TerminalPalette.Background, new Rect(Bounds.Size));
        if (Buffer is not { } buffer)
        {
            return;
        }

        var top = TopLine;
        for (var row = 0; row < buffer.Rows; row++)
        {
            var y = Padding / 2 + row * _metrics.LineHeight;
            var shaped = Shape(buffer.LineAt(top + row));
            foreach (var run in shaped.Runs)
            {
                var x = Padding + run.Column * _metrics.CharWidth;
                var width = run.Cells * _metrics.CharWidth;
                if (run.Background is not null)
                {
                    context.FillRectangle(run.Background, new Rect(x, y, width, _metrics.LineHeight));
                }

                if (run.Text is not null)
                {
                    using (context.PushOpacity(run.Opacity))
                    {
                        run.Text.Draw(context, new Point(x, y + _metrics.TextTop));
                    }
                }
            }
        }

        DrawMatches(context, buffer, top);
        DrawHoverLink(context, top);
        DrawCommandMarks(context, buffer, top);
        DrawSelection(context, buffer, top);
        DrawCursor(context, buffer);
    }

    /// <summary>Find matches on screen, the current one stronger and outlined (like find in the editor).</summary>
    private void DrawMatches(DrawingContext context, TerminalBuffer buffer, int top)
    {
        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            if (match.Line < top || match.Line >= top + buffer.Rows)
            {
                continue;
            }

            var rect = new Rect(
                Padding + match.StartColumn * _metrics.CharWidth,
                Padding / 2 + (match.Line - top) * _metrics.LineHeight,
                (match.EndColumn - match.StartColumn) * _metrics.CharWidth,
                _metrics.LineHeight);
            var current = i == _currentMatch;
            context.DrawRectangle(current ? Palette.SearchMatchCurrent : Palette.SearchMatch, current ? CurrentMatchOutline : null, rect);
        }
    }

    private void DrawSelection(DrawingContext context, TerminalBuffer buffer, int top)
    {
        if (!HasSelection)
        {
            return;
        }

        var (start, end) = OrderedSelection();
        for (var line = Math.Max(start.Line, top); line <= Math.Min(end.Line, top + buffer.Rows - 1); line++)
        {
            var from = line == start.Line ? start.Column : 0;
            var to = line == end.Line ? end.Column : buffer.Columns;
            var y = Padding / 2 + (line - top) * _metrics.LineHeight;
            context.FillRectangle(Palette.TextSelection, new Rect(Padding + from * _metrics.CharWidth, y, (to - from) * _metrics.CharWidth, _metrics.LineHeight));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalBuffer buffer)
    {
        if (!buffer.CursorVisible || _scrollOffset > 0 || _session?.HasExited != false)
        {
            return;
        }

        var rect = new Rect(
            Padding + buffer.CursorColumn * _metrics.CharWidth,
            Padding / 2 + buffer.CursorRow * _metrics.LineHeight,
            _metrics.CharWidth,
            _metrics.LineHeight);

        if (IsFocused && _cursorOn)
        {
            context.FillRectangle(CursorFill, rect);
        }
        else if (!IsFocused)
        {
            context.DrawRectangle(null, CursorOutline, rect.Deflate(0.5));
        }
    }

    private ShapedRow Shape(TerminalLine line)
    {
        if (_rowCache.TryGetValue(line, out var cached) && cached.Version == line.Version && ReferenceEquals(cached.Metrics, _metrics))
        {
            return cached;
        }

        var shaped = new ShapedRow(line.Version, _metrics, BuildRuns(line.Cells));
        _rowCache.AddOrUpdate(line, shaped);
        return shaped;
    }

    /// <summary>Splits a row into runs of cells with the same style. A wide character is always its own run.</summary>
    private List<RowRun> BuildRuns(TerminalCell[] cells)
    {
        var runs = new List<RowRun>();
        var text = new StringBuilder();
        var column = 0;
        while (column < cells.Length)
        {
            var cell = cells[column];
            if (cell.IsContinuation)
            {
                column++;
                continue;
            }

            var start = column;
            text.Clear();
            if (cell.Width == 2)
            {
                text.Append(char.ConvertFromUtf32(cell.Rune));
                column += 2;
            }
            else
            {
                while (column < cells.Length && cells[column].Width == 1 && cells[column].Style == cell.Style)
                {
                    text.Append(cells[column].Rune == 0 ? " " : char.ConvertFromUtf32(cells[column].Rune));
                    column++;
                }
            }

            runs.Add(CreateRun(start, column - start, text.ToString(), cell.Style));
        }

        return runs;
    }

    private RowRun CreateRun(int column, int cellCount, string text, CellStyle style)
    {
        var attributes = style.Attributes;

        // Bold text in one of the 8 basic colors uses its bright variant, as most terminals do.
        var foregroundColor = attributes.HasFlag(CellAttributes.Bold) && style.Foreground is { Kind: TerminalColorKind.Indexed, Value: < 8 }
            ? TerminalColor.Indexed(style.Foreground.Value + 8)
            : style.Foreground;
        var foreground = TerminalPalette.Resolve(foregroundColor, TerminalPalette.Foreground);
        IBrush? background = style.Background.Kind == TerminalColorKind.Default ? null : TerminalPalette.Resolve(style.Background, TerminalPalette.Background);
        if (attributes.HasFlag(CellAttributes.Inverse))
        {
            (foreground, background) = (background ?? TerminalPalette.Background, foreground);
        }

        var blank = string.IsNullOrWhiteSpace(text) && !attributes.HasFlag(CellAttributes.Underline);
        if (blank || attributes.HasFlag(CellAttributes.Hidden))
        {
            return new RowRun(column, cellCount, null, background, 1);
        }

        var typeface = new Typeface(Typography.Code,
            attributes.HasFlag(CellAttributes.Italic) ? FontStyle.Italic : FontStyle.Normal,
            attributes.HasFlag(CellAttributes.Bold) ? FontWeight.Bold : FontWeight.Normal);
        var decorations = attributes.HasFlag(CellAttributes.Underline) ? TextDecorations.Underline
            : attributes.HasFlag(CellAttributes.Strikethrough) ? TextDecorations.Strikethrough
            : null;

        // Characters the coding font lacks (Arabic, CJK, emoji) get an explicit font, as in the editor.
        var fonts = GlyphFallback.FallbackRuns(text)
            .Select(run => new ValueSpan<TextRunProperties>(run.Start, run.Length, _metrics.RunProperties(foreground, run.Typeface)))
            .ToList();
        var layout = new TextLayout(text, typeface, _metrics.FontSize, foreground, textDecorations: decorations, textStyleOverrides: fonts);
        return new RowRun(column, cellCount, layout, background, attributes.HasFlag(CellAttributes.Dim) ? 0.6 : 1);
    }

    private sealed record ShapedRow(int Version, EditorMetrics Metrics, List<RowRun> Runs);

    private sealed record RowRun(int Column, int Cells, TextLayout? Text, IBrush? Background, double Opacity);
}
