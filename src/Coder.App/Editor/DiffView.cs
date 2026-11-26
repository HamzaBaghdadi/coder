using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Syntax;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.App.Editor;

/// <summary>
/// A read-only side-by-side diff: the original on the left, the modified text on the right, aligned
/// row by row with fillers where one side has no line. Removed lines are tinted red, added lines
/// green, and inside a changed line the part that differs is tinted stronger. Both sides scroll
/// together. Like the editor, it implements <see cref="ILogicalScrollable"/> and draws only the
/// visible rows, so large files cost no more than small ones.
/// </summary>
public sealed class DiffView : Control, ILogicalScrollable
{
    private const double GutterPadding = 12;
    private const double TextPadding = 8;
    private const int TabSize = 4;
    private const int WheelLines = 3;

    private static readonly IBrush RemovedLine = Palette.DiffRemovedLine;
    private static readonly IBrush RemovedText = Palette.DiffRemovedText;
    private static readonly IBrush AddedLine = Palette.DiffInsertedLine;
    private static readonly IBrush AddedText = Palette.DiffInsertedText;
    private static readonly IBrush Filler = Palette.DiffFiller;
    private static readonly Pen DividerPen = new(Palette.Border, 1);

    private readonly EditorMetrics _metrics = new(new Typeface(Typography.Code), 13.5, Typography.LineHeightFactor);
    private DiffTabViewModel? _diff;
    private Side _left = Side.Empty;
    private Side _right = Side.Empty;
    private Vector _offset;
    private Size _viewport;
    private EventHandler? _scrollInvalidated;

    static DiffView()
    {
        FocusableProperty.OverrideDefaultValue<DiffView>(true);
    }

    public DiffTabViewModel? Diff
    {
        get => _diff;
        set
        {
            _diff = value;
            _left = value is null ? Side.Empty : new Side(value.OriginalLines, value.Language);
            _right = value is null ? Side.Empty : new Side(value.ModifiedLines, value.Language);
            _offset = default;
            _scrollInvalidated?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>Scrolls so <paramref name="row"/> is a few rows below the top.</summary>
    public void RevealRow(int row)
    {
        Offset = new Vector(_offset.X, Math.Max(0, (row - 3) * _metrics.LineHeight));
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The first visible row (for "next change" navigation).</summary>
    public int FirstVisibleRow => (int)(_offset.Y / _metrics.LineHeight);

    // ILogicalScrollable ----------------------------------------------------------------------------

    event EventHandler? ILogicalScrollable.ScrollInvalidated
    {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public bool IsLogicalScrollEnabled => true;

    /// <summary>One wheel tick: 3 lines, like VS Code (also the scroll bar arrow step).</summary>
    public Size ScrollSize => new(_metrics.CharWidth * 4 * WheelLines, _metrics.LineHeight * WheelLines);

    public Size PageScrollSize => new(_viewport.Width / 2, _viewport.Height - _metrics.LineHeight);

    public Size Extent
    {
        get
        {
            var rows = _diff?.Rows.Count ?? 0;
            var widest = Math.Max(_left.WidestColumns, _right.WidestColumns) * _metrics.CharWidth;
            var paneText = PaneWidth - GutterWidth - TextPadding;
            var width = _viewport.Width + Math.Max(0, widest + _metrics.CharWidth * 4 - paneText);
            return new Size(width, Math.Max(_viewport.Height, (rows - 1) * _metrics.LineHeight + _viewport.Height));
        }
    }

    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set
        {
            var extent = Extent;
            var clamped = new Vector(
                Math.Round(Math.Clamp(value.X, 0, Math.Max(0, extent.Width - _viewport.Width))),
                Math.Round(Math.Clamp(value.Y, 0, Math.Max(0, extent.Height - _viewport.Height))));
            if (clamped != _offset)
            {
                _offset = clamped;
                InvalidateVisual();
            }
        }
    }

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            _scrollInvalidated?.Invoke(this, EventArgs.Empty);
        }

        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    // Drawing -----------------------------------------------------------------------------------

    private double PaneWidth => Math.Floor(_viewport.Width / 2);

    private double GutterWidth => GutterPadding * 2 + Math.Max(3, Math.Max(_left.Lines.Count, _right.Lines.Count).ToString(CultureInfo.InvariantCulture).Length) * _metrics.CharWidth;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Palette.EditorBackground, bounds);
        if (_diff is null)
        {
            return;
        }

        var rows = _diff.Rows;
        var lineHeight = _metrics.LineHeight;
        var first = Math.Max(0, (int)(_offset.Y / lineHeight));
        var last = Math.Min(rows.Count - 1, (int)((_offset.Y + bounds.Height) / lineHeight));
        var pane = PaneWidth;
        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var top = i * lineHeight - _offset.Y;
            var (leftBrush, rightBrush) = row.Kind switch
            {
                DiffRowKind.Removed => (RemovedLine, Filler),
                DiffRowKind.Added => (Filler, AddedLine),
                DiffRowKind.Modified => (RemovedLine, AddedLine),
                _ => ((IBrush?)null, (IBrush?)null),
            };

            var (leftChange, rightChange) = row.Kind == DiffRowKind.Modified
                ? ChangedSpans(_left.Lines[row.OldLine!.Value], _right.Lines[row.NewLine!.Value])
                : (default, default);
            DrawCell(context, _left, row.OldLine, 0, top, pane, leftBrush, leftChange, RemovedText);
            DrawCell(context, _right, row.NewLine, pane, top, bounds.Width - pane, rightBrush, rightChange, AddedText);
        }

        context.DrawLine(DividerPen, new Point(pane + 0.5, 0), new Point(pane + 0.5, bounds.Height));
    }

    /// <summary>One side of one row: background, line number, then the syntax-colored text.</summary>
    private void DrawCell(DrawingContext context, Side side, int? line, double left, double top, double width,
        IBrush? background, (int Start, int End) changed, IBrush changedBrush)
    {
        var cell = new Rect(left, top, width, _metrics.LineHeight);
        if (background is not null)
        {
            context.FillRectangle(background, cell);
        }

        if (line is not { } index)
        {
            return;
        }

        var gutter = GutterWidth;
        var number = new FormattedText((index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _metrics.Typeface, _metrics.FontSize - 1, Palette.LineNumber);
        context.DrawText(number, new Point(left + gutter - GutterPadding - number.Width, top + _metrics.TextTop + 0.5));

        var textLeft = left + gutter + TextPadding - _offset.X;
        using (context.PushClip(new Rect(left + gutter, top, Math.Max(0, width - gutter), _metrics.LineHeight)))
        {
            if (changed.End > changed.Start)
            {
                var x = textLeft + changed.Start * _metrics.CharWidth;
                context.FillRectangle(changedBrush, new Rect(x, top, (changed.End - changed.Start) * _metrics.CharWidth, _metrics.LineHeight));
            }

            context.DrawText(side.Text(index, _metrics), new Point(textLeft, top + _metrics.TextTop));
        }
    }

    /// <summary>The differing middle of two lines (after their common start and end), in display columns.</summary>
    private static ((int, int) Left, (int, int) Right) ChangedSpans(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }

        return ((prefix, before.Length - suffix), (prefix, after.Length - suffix));
    }

    /// <summary>
    /// One side's lines with tabs expanded, lexed once from the top (so block comments color
    /// correctly), and their shaped text cached while they are on screen.
    /// </summary>
    private sealed class Side
    {
        public static readonly Side Empty = new([], Languages.PlainText);

        private readonly LineLexer _lexer;
        private readonly LexState[] _states;
        private readonly Dictionary<int, FormattedText> _shaped = [];

        public Side(IReadOnlyList<string> lines, LanguageDefinition language)
        {
            Lines = lines.Select(line => line.Contains('\t', StringComparison.Ordinal) ? TextColumns.ExpandTabs(line, TabSize) : line).ToList();
            WidestColumns = Lines.Count == 0 ? 0 : Lines.Max(l => l.Length);

            // The lexer state at the start of each line.
            _lexer = new LineLexer(language);
            var tokens = new List<Token>();
            _states = new LexState[Lines.Count];
            var state = LexState.Normal;
            for (var i = 0; i < Lines.Count; i++)
            {
                _states[i] = state;
                tokens.Clear();
                state = _lexer.Tokenize(Lines[i], state, tokens);
            }
        }

        public IReadOnlyList<string> Lines { get; }

        public int WidestColumns { get; }

        public FormattedText Text(int line, EditorMetrics metrics)
        {
            if (_shaped.TryGetValue(line, out var text))
            {
                return text;
            }

            if (_shaped.Count > 400)
            {
                _shaped.Clear(); // only on-screen lines need to stay shaped
            }

            var content = Lines[line];
            text = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, metrics.Typeface, metrics.FontSize, SyntaxColors.For(TokenKind.Text));
            var tokens = new List<Token>();
            _lexer.Tokenize(content, _states[line], tokens);
            foreach (var token in tokens)
            {
                text.SetForegroundBrush(SyntaxColors.For(token.Kind), token.Start, token.Length);
            }

            // The same explicit font fallback as the editor (Arabic, CJK, emoji).
            foreach (var run in GlyphFallback.FallbackRuns(content))
            {
                text.SetFontTypeface(run.Typeface, run.Start, run.Length);
            }

            _shaped[line] = text;
            return text;
        }
    }
}
