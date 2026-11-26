using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Coder.App.Theme;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>Painting. Only lines inside the viewport are touched, so cost is independent of file size.</summary>
public sealed partial class EditorView
{
    private const int BracketSearchLimit = 20_000;

    private static readonly Pen IndentGuidePen = new(Palette.IndentGuide, 1);
    private static readonly Pen BracketPen = new(Palette.BracketMatchBorder, 1);
    private static readonly Pen SearchMatchPen = new(Palette.SearchMatchBorder, 1);
    private static readonly Pen PreeditUnderline = new(Palette.Caret, 1);

    private readonly LineNumberCache _lineNumbers = new();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Palette.EditorBackground, bounds);
        if (_editor is null || _highlighter is null)
        {
            return;
        }

        var document = _editor.Document;
        var lineHeight = _metrics.LineHeight;
        var firstLine = Math.Max(0, (int)(_offset.Y / lineHeight));
        var lastLine = Math.Min(document.LineCount - 1, (int)((_offset.Y + bounds.Height) / lineHeight));
        var textLeft = TextLeft;
        var visible = VisibleSelections(firstLine, lastLine);
        var caretLines = visible.Select(s => document.GetLineOfOffset(s.Active)).ToHashSet();

        using (context.PushClip(new Rect(GutterWidth, 0, Math.Max(0, bounds.Width - GutterWidth), bounds.Height)))
        {
            DrawConflicts(context, firstLine, lastLine, bounds.Width);
            DrawCurrentLines(context, visible, bounds.Width);
            DrawSearchMatches(context, firstLine, lastLine);
            DrawSelections(context, visible, firstLine, lastLine);
            DrawIndentGuides(context, firstLine, lastLine, textLeft);
            DrawBracketMatch(context);
            DrawText(context, firstLine, lastLine);
            DrawCtrlLink(context);
            DrawDiagnostics(context, firstLine, lastLine);
            DrawBlame(context);
            DrawPreedit(context);
            DrawCarets(context, visible);
        }

        DrawGutter(context, firstLine, lastLine, caretLines, bounds.Height);
        DrawMinimap(context);
        _layouts.Trim(firstLine, lastLine);
    }

    private double LineTop(int line) => line * _metrics.LineHeight - _offset.Y;

    private double ColumnX(int visualColumn, double textLeft) => textLeft + visualColumn * _metrics.CharWidth - _offset.X;

    /// <summary>The selections (cursors) that touch the visible lines; with many cursors the rest are skipped.</summary>
    private List<Selection> VisibleSelections(int firstLine, int lastLine)
    {
        var document = _editor!.Document;
        var visibleStart = document.GetLineStart(firstLine);
        var visibleEnd = document.GetLineEnd(lastLine);
        return _editor.Selections.All.Where(s => s.End >= visibleStart && s.Start <= visibleEnd).ToList();
    }

    /// <summary>Highlights the line of every cursor that has no selection.</summary>
    private void DrawCurrentLines(DrawingContext context, List<Selection> visible, double width)
    {
        foreach (var line in visible.Where(s => s.IsEmpty).Select(s => _editor!.Document.GetLineOfOffset(s.Active)).Distinct())
        {
            context.FillRectangle(Palette.CurrentLine, new Rect(0, LineTop(line), width, _metrics.LineHeight));
        }
    }

    private void DrawSelections(DrawingContext context, List<Selection> visible, int firstLine, int lastLine)
    {
        var brush = IsFocused ? Palette.TextSelection : Palette.TextSelectionInactive;
        foreach (var selection in visible.Where(s => !s.IsEmpty))
        {
            DrawRange(context, selection.Start, selection.End, brush, null, firstLine, lastLine);
        }
    }

    /// <summary>Highlights find results that intersect the viewport; the current one is stronger and outlined.</summary>
    private void DrawSearchMatches(DrawingContext context, int firstLine, int lastLine)
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        var document = _editor!.Document;
        var visibleStart = document.GetLineStart(firstLine);
        var visibleEnd = document.GetLineEnd(lastLine);

        // Matches are sorted, so jump straight to the first one that can be visible.
        int low = 0, high = _searchMatches.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_searchMatches[mid].End < visibleStart)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (var i = low; i < _searchMatches.Count && _searchMatches[i].Start <= visibleEnd; i++)
        {
            var match = _searchMatches[i];
            var isCurrent = i == _currentSearchMatch;
            DrawRange(context, match.Start, match.End,
                isCurrent ? Palette.SearchMatchCurrent : Palette.SearchMatch,
                isCurrent ? SearchMatchPen : null,
                firstLine, lastLine);
        }
    }

    /// <summary>
    /// Fills a document range line by line, following the real glyph positions (a right-to-left run can
    /// split one line of the range into several boxes). A selected line break shows as one extra cell.
    /// </summary>
    private void DrawRange(DrawingContext context, int startOffset, int endOffset, IBrush brush, Pen? pen, int firstLine, int lastLine)
    {
        var document = _editor!.Document;
        var start = document.GetPosition(startOffset);
        var end = document.GetPosition(endOffset);

        for (var line = Math.Max(firstLine, start.Line); line <= Math.Min(lastLine, end.Line); line++)
        {
            var from = line == start.Line ? start.Column : 0;
            var to = line == end.Line ? end.Column : document.GetLineLength(line);
            var top = LineTop(line);
            foreach (var (left, right) in SpansOf(line, from, to))
            {
                context.DrawRectangle(brush, pen, new Rect(left, top, right - left, _metrics.LineHeight), 3, 3);
            }

            if (line < end.Line)
            {
                context.DrawRectangle(brush, pen, new Rect(LineEndX(line), top, _metrics.CharWidth, _metrics.LineHeight), 3, 3);
            }
        }
    }

    /// <summary>Thin vertical lines at each indentation level, the main visual aid for nesting.</summary>
    private void DrawIndentGuides(DrawingContext context, int firstLine, int lastLine, double textLeft)
    {
        var document = _editor!.Document;
        var tabSize = TabSize;
        var previousIndent = 0;
        for (var line = firstLine; line <= lastLine; line++)
        {
            var text = document.GetLineText(line);
            var indent = TextColumns.ToVisual(text, TextColumns.LeadingWhitespaceLength(text), tabSize);

            // Blank lines continue the guides of the block they sit in.
            if (string.IsNullOrWhiteSpace(text))
            {
                indent = previousIndent;
            }

            previousIndent = indent;
            var top = LineTop(line);
            for (var column = 0; column < indent; column += tabSize)
            {
                var x = Math.Round(ColumnX(column, textLeft)) + 0.5;
                context.DrawLine(IndentGuidePen, new Point(x, top), new Point(x, top + _metrics.LineHeight));
            }
        }
    }

    private void DrawText(DrawingContext context, int firstLine, int lastLine)
    {
        for (var line = firstLine; line <= lastLine; line++)
        {
            var layout = LayoutOf(line);
            layout.Layout.Draw(context, new Point(LayoutLeft(layout), LineTop(line) + _metrics.TextTop));
            _widestLine = Math.Max(_widestLine, layout.StartColumn * _metrics.CharWidth + layout.Width);
        }
    }

    private void DrawCarets(DrawingContext context, List<Selection> visible)
    {
        if (!IsFocused || !_caretVisible || _preedit is not null)
        {
            return;
        }

        foreach (var selection in visible)
        {
            var caret = _editor!.Document.GetPosition(selection.Active);
            var x = Math.Round(CaretX(caret.Line, caret.Column));
            context.FillRectangle(Palette.Caret, new Rect(x - 1, LineTop(caret.Line) + 2, 2, _metrics.LineHeight - 4));
        }
    }

    private void DrawBracketMatch(DrawingContext context)
    {
        if (FindBracketPair() is not var (open, close))
        {
            return;
        }

        foreach (var offset in (ReadOnlySpan<int>)[open, close])
        {
            var position = _editor!.Document.GetPosition(offset);
            foreach (var (left, right) in SpansOf(position.Line, position.Column, position.Column + 1))
            {
                var rect = new Rect(left, LineTop(position.Line) + 1, right - left, _metrics.LineHeight - 2);
                context.DrawRectangle(Palette.BracketMatch, BracketPen, rect, 2, 2);
            }
        }
    }

    private void DrawGutter(DrawingContext context, int firstLine, int lastLine, HashSet<int> caretLines, double height)
    {
        var gutterWidth = GutterWidth;
        context.FillRectangle(Palette.EditorBackground, new Rect(0, 0, gutterWidth, height));

        var numberRight = gutterWidth - GutterPaddingRight;
        for (var line = firstLine; line <= lastLine; line++)
        {
            var number = _lineNumbers.Get(line + 1, caretLines.Contains(line), _metrics);
            context.DrawText(number, new Point(numberRight - number.Width, LineTop(line) + _metrics.TextTop));
        }

        DrawLineChanges(context, firstLine, lastLine);
    }

    /// <summary>
    /// Text being composed in an input method (Chinese, Japanese, Korean...), drawn at the caret with an
    /// underline until the input method commits it. It is not part of the document yet.
    /// </summary>
    private void DrawPreedit(DrawingContext context)
    {
        if (_preedit is not { Length: > 0 } preedit)
        {
            return;
        }

        var caret = _editor!.CaretPosition;
        var x = CaretX(caret.Line, caret.Column);
        var top = LineTop(caret.Line);
        // Composed text is usually CJK, so it gets the same explicit font fallback as the lines.
        var fonts = GlyphFallback.FallbackRuns(preedit)
            .Select(run => new ValueSpan<TextRunProperties>(run.Start, run.Length, _metrics.RunProperties(Palette.TextPrimary, run.Typeface)))
            .ToList();
        var text = new TextLayout(preedit, _metrics.Typeface, _metrics.FontSize, Palette.TextPrimary, textStyleOverrides: fonts);
        var width = text.WidthIncludingTrailingWhitespace;
        context.FillRectangle(Palette.Elevated, new Rect(x, top, width + 2, _metrics.LineHeight));
        text.Draw(context, new Point(x + 1, top + _metrics.TextTop));
        context.DrawLine(PreeditUnderline, new Point(x + 1, top + _metrics.LineHeight - 3), new Point(x + 1 + width, top + _metrics.LineHeight - 3));
    }

    /// <summary>
    /// Shaped line-number labels, reused across frames. Re-shaping every visible number on every repaint
    /// (each mouse move while selecting repaints) is wasted work.
    /// </summary>
    private sealed class LineNumberCache
    {
        private const int MaxEntries = 1_000;

        private readonly Dictionary<(int Number, bool Active), FormattedText> _labels = [];
        private EditorMetrics? _metrics;

        public FormattedText Get(int number, bool active, EditorMetrics metrics)
        {
            if (!ReferenceEquals(metrics, _metrics) || _labels.Count > MaxEntries)
            {
                _labels.Clear();
                _metrics = metrics;
            }

            if (!_labels.TryGetValue((number, active), out var label))
            {
                label = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, metrics.Typeface, metrics.FontSize, active ? Palette.LineNumberActive : Palette.LineNumber);
                _labels[(number, active)] = label;
            }

            return label;
        }
    }

    /// <summary>Finds the bracket next to the caret and its partner, honoring nesting.</summary>
    private (int Open, int Close)? FindBracketPair()
    {
        var document = _editor!.Document;
        var brackets = _editor.Language.Brackets;
        var caret = _editor.Selection.Active;

        foreach (var at in (ReadOnlySpan<int>)[caret - 1, caret])
        {
            if (at < 0 || at >= document.Length)
            {
                continue;
            }

            var index = brackets.IndexOf(document[at], StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var isOpen = index % 2 == 0;
            var self = brackets[index];
            var partner = isOpen ? brackets[index + 1] : brackets[index - 1];
            if (FindPartner(at, self, partner, isOpen ? 1 : -1) is { } match)
            {
                return isOpen ? (at, match) : (match, at);
            }
        }

        return null;
    }

    private int? FindPartner(int from, char self, char partner, int direction)
    {
        var document = _editor!.Document;
        var depth = 0;
        for (int i = from, steps = 0; i >= 0 && i < document.Length && steps < BracketSearchLimit; i += direction, steps++)
        {
            var c = document[i];
            if (c == self)
            {
                depth++;
            }
            else if (c == partner && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }
}
