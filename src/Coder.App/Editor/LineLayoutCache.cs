using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Coder.App.Theme;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// Text drawn inside a line that is not part of the document (an inlay hint), placed before the
/// character at display index <see cref="At"/>. <see cref="CaretAfter"/>: a caret at that index sits
/// after the hint (a parameter name before its argument) rather than before it (a type after a name).
/// <see cref="IsSuggestion"/>: an AI inline suggestion (gray, no background) rather than an inlay hint.
/// </summary>
internal readonly record struct LineInsertion(int At, string Label, bool PadLeft, bool PadRight, bool CaretAfter, bool IsSuggestion = false)
{
    /// <summary>The label with its padding: the space the hint takes in the line.</summary>
    public string Text => (PadLeft ? " " : string.Empty) + Label + (PadRight ? " " : string.Empty);
}

/// <summary>
/// The shaped, colored text of one line and its geometry. Positions are display indexes: indexes into
/// the line with tabs expanded to spaces (see <see cref="TextColumns"/>). Inlay hints are shaped into
/// the same text; positions skip over them, so carets, selections and clicks only ever land on real text.
/// <para>
/// Real glyph positions are used, not a fixed character grid, so wide characters (CJK, emoji), shaped
/// scripts (Arabic) and right-to-left runs all place the caret, selection and clicks correctly.
/// </para>
/// </summary>
internal sealed class LineLayout(TextLayout layout, int startColumn, IReadOnlyList<LineInsertion>? insertions = null)
{
    private readonly IReadOnlyList<LineInsertion> _insertions = insertions ?? [];

    public TextLayout Layout { get; } = layout;

    /// <summary>First display index in <see cref="Layout"/>; non-zero only for very long, sliced lines.</summary>
    public int StartColumn { get; } = startColumn;

    /// <summary>Display length (without inserted hints).</summary>
    public int Length { get; } = layout.TextLines.Sum(line => line.Length) - (insertions ?? []).Sum(i => i.Text.Length);

    public double Width => Layout.WidthIncludingTrailingWhitespace;

    /// <summary>True when <paramref name="displayIndex"/> falls inside this layout (or at its end).</summary>
    public bool Covers(int displayIndex) => displayIndex >= StartColumn && displayIndex <= StartColumn + Length;

    /// <summary>X of the caret before <paramref name="displayIndex"/>, relative to the layout origin.</summary>
    public double XOf(int displayIndex) => Layout.HitTestTextPosition(ToLayout(displayIndex - StartColumn, i => i.CaretAfter)).X;

    /// <summary>The caret position (display index) nearest to <paramref name="x"/>, relative to the layout origin.</summary>
    public int IndexAt(double x) => StartColumn + FromLayout(Layout.HitTestPoint(new Point(x, Layout.Height / 2)).TextPosition);

    /// <summary>
    /// The horizontal spans covering a range. Right-to-left text inside a line can turn one logical
    /// range into several visual spans. Hints at the range's edges stay outside it.
    /// </summary>
    public IEnumerable<(double Left, double Right)> SpansOf(int startIndex, int endIndex)
    {
        var start = Math.Max(startIndex, StartColumn) - StartColumn;
        var end = Math.Min(endIndex, StartColumn + Length) - StartColumn;
        if (end <= start)
        {
            return [];
        }

        var (layoutStart, layoutEnd) = (ToLayout(start, _ => true), ToLayout(end, _ => false));
        return Layout.HitTestTextRange(layoutStart, layoutEnd - layoutStart).Select(rect => (rect.Left, rect.Right));
    }

    /// <summary>
    /// Display index → index in the shaped text: past the hints before it, and past those exactly at it
    /// when <paramref name="countsAt"/> says so.
    /// </summary>
    private int ToLayout(int index, Func<LineInsertion, bool> countsAt) =>
        index + _insertions.Where(i => i.At < index || (i.At == index && countsAt(i))).Sum(i => i.Text.Length);

    /// <summary>Index in the shaped text → display index; a click on a hint lands at its position.</summary>
    private int FromLayout(int layoutIndex)
    {
        var shift = 0;
        foreach (var insertion in _insertions)
        {
            var hintStart = insertion.At + shift;
            if (layoutIndex < hintStart)
            {
                break;
            }

            if (layoutIndex < hintStart + insertion.Text.Length)
            {
                return insertion.At;
            }

            shift += insertion.Text.Length;
        }

        return layoutIndex - shift;
    }
}

/// <summary>
/// Caches the layouts of recently drawn lines. Only lines near the viewport are kept, so memory stays
/// flat no matter how large the document is. Edits evict from the edited line down.
/// </summary>
internal sealed class LineLayoutCache
{
    /// <summary>Lines longer than this are shaped per frame for the visible slice only, never cached.</summary>
    public const int MaxCachedLineLength = 2_000;

    private const int KeepAroundViewport = 80;

    private readonly Dictionary<int, LineLayout> _lines = [];
    private readonly List<Token> _tokens = [];

    public void Clear() => _lines.Clear();

    /// <summary>Drops one line (its inline suggestion came or went).</summary>
    public void Invalidate(int line) => _lines.Remove(line);

    /// <summary>Drops every cached line at or after <paramref name="firstLine"/>.</summary>
    public void InvalidateFrom(int firstLine)
    {
        foreach (var line in _lines.Keys.Where(line => line >= firstLine).ToList())
        {
            _lines.Remove(line);
        }
    }

    /// <summary>Forgets lines far away from the viewport.</summary>
    /// <param name="keep">Lines drawn elsewhere (the sticky scroll's pinned headers), kept too.</param>
    public void Trim(int firstVisible, int lastVisible, IReadOnlySet<int>? keep = null)
    {
        if (_lines.Count < (lastVisible - firstVisible) + KeepAroundViewport * 2)
        {
            return;
        }

        foreach (var line in _lines.Keys.Where(line => (line < firstVisible - KeepAroundViewport || line > lastVisible + KeepAroundViewport) && keep?.Contains(line) != true).ToList())
        {
            _lines.Remove(line);
        }
    }

    /// <summary>
    /// Returns the layout for <paramref name="line"/>. Normal lines are laid out whole and cached; very
    /// long lines are sliced to the columns from <paramref name="startColumn"/> that are on screen.
    /// </summary>
    /// <param name="insertions">Inlay hints by display index, in order (left out of very long, sliced lines).</param>
    public LineLayout Get(int line, TextDocument document, SyntaxHighlighter highlighter, EditorMetrics metrics, int tabSize, int startColumn, int columnCount,
        IReadOnlyList<LineInsertion> insertions)
    {
        if (_lines.TryGetValue(line, out var cached))
        {
            return cached;
        }

        var raw = document.GetLineText(line);
        var display = TextColumns.ExpandTabs(raw, tabSize);
        var isLong = display.Length > MaxCachedLineLength;
        var sliceStart = isLong ? Math.Min(startColumn, display.Length) : 0;
        var sliceLength = isLong ? Math.Min(columnCount, display.Length - sliceStart) : display.Length;

        var text = display.Substring(sliceStart, sliceLength);
        var runs = StyleRuns(line, raw, text, highlighter, metrics, tabSize, sliceStart);
        if (!isLong && insertions.Count > 0)
        {
            (text, runs) = Insert(text, runs, insertions, metrics);
        }
        else
        {
            insertions = [];
        }

        var layout = new LineLayout(
            new TextLayout(text, metrics.Typeface, metrics.FontSize, Palette.TextPrimary, textStyleOverrides: runs),
            sliceStart,
            insertions);

        if (!isLong)
        {
            _lines[line] = layout;
        }

        return layout;
    }

    /// <summary>
    /// Shapes inlay hints into the line: each hint's text goes before its character, in the hint color on
    /// a faint background (its padding stays clear), and the line's own runs move along to stay on their text.
    /// </summary>
    private static (string Text, List<ValueSpan<TextRunProperties>> Runs) Insert(
        string text, List<ValueSpan<TextRunProperties>> runs, IReadOnlyList<LineInsertion> insertions, EditorMetrics metrics)
    {
        // Where a display index lands in the combined text: after every hint placed at or before it.
        int Shift(int index) => index + insertions.Where(i => i.At <= index).Sum(i => i.Text.Length);

        var combined = new System.Text.StringBuilder(text.Length + insertions.Sum(i => i.Text.Length));
        var result = new List<ValueSpan<TextRunProperties>>(runs.Count + insertions.Count * 3);
        var hintText = new GenericTextRunProperties(metrics.Typeface, metrics.FontSize, foregroundBrush: Palette.InlayHint, backgroundBrush: Palette.InlayHintBackground);
        var suggestionText = metrics.RunProperties(Palette.InlineSuggestion);
        var plain = metrics.RunProperties(Palette.TextPrimary);
        var from = 0;
        foreach (var insertion in insertions)
        {
            var at = Math.Clamp(insertion.At, 0, text.Length);
            combined.Append(text, from, at - from);
            var start = combined.Length;
            combined.Append(insertion.Text);
            var labelStart = start + (insertion.PadLeft ? 1 : 0);
            if (insertion.PadLeft)
            {
                result.Add(new ValueSpan<TextRunProperties>(start, 1, plain));
            }

            result.Add(new ValueSpan<TextRunProperties>(labelStart, insertion.Label.Length, insertion.IsSuggestion ? suggestionText : hintText));
            if (insertion.PadRight)
            {
                result.Add(new ValueSpan<TextRunProperties>(labelStart + insertion.Label.Length, 1, plain));
            }

            from = at;
        }

        combined.Append(text, from, text.Length - from);

        // The line's runs, cut where hints come between their characters.
        foreach (var run in runs)
        {
            var pieceStart = run.Start;
            var runEnd = run.Start + run.Length;
            foreach (var cut in insertions.Select(i => i.At).Where(at => at > run.Start && at < runEnd).Distinct().Order())
            {
                result.Add(new ValueSpan<TextRunProperties>(Shift(pieceStart), cut - pieceStart, run.Value));
                pieceStart = cut;
            }

            result.Add(new ValueSpan<TextRunProperties>(Shift(pieceStart), runEnd - pieceStart, run.Value));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (combined.ToString(), result);
    }

    /// <summary>
    /// Style runs over the (tab-expanded, possibly sliced) display text: syntax colors, combined with
    /// an explicit font wherever the coding font has no glyph (see <see cref="GlyphFallback"/>).
    /// </summary>
    private List<ValueSpan<TextRunProperties>> StyleRuns(int line, string raw, string text, SyntaxHighlighter highlighter, EditorMetrics metrics, int tabSize, int sliceStart)
    {
        var colors = TokenColors(line, raw, highlighter, tabSize, sliceStart, text.Length);
        var fonts = GlyphFallback.FallbackRuns(text);

        // Cut the line at every color and font boundary; each piece gets its color and font.
        var cuts = new SortedSet<int> { 0, text.Length };
        foreach (var (start, length, _) in colors)
        {
            cuts.Add(start);
            cuts.Add(start + length);
        }

        foreach (var (start, length, _) in fonts)
        {
            cuts.Add(start);
            cuts.Add(start + length);
        }

        var runs = new List<ValueSpan<TextRunProperties>>(cuts.Count);
        int? previous = null;
        foreach (var cut in cuts)
        {
            if (previous is { } from && cut > from)
            {
                var brush = colors.FirstOrDefault(c => c.Start <= from && from < c.Start + c.Length).Brush;
                var font = fonts.FirstOrDefault(f => f.Start <= from && from < f.Start + f.Length).Typeface;
                if (brush is not null || font != default)
                {
                    runs.Add(new ValueSpan<TextRunProperties>(from, cut - from, metrics.RunProperties(brush ?? Palette.TextPrimary, font)));
                }
            }

            previous = cut;
        }

        return runs;
    }

    /// <summary>Syntax-colored spans of the display text (plain text has none).</summary>
    private List<(int Start, int Length, IBrush Brush)> TokenColors(int line, string raw, SyntaxHighlighter highlighter, int tabSize, int sliceStart, int sliceLength)
    {
        highlighter.GetTokens(line, _tokens);
        var hasTabs = raw.Contains('\t', StringComparison.Ordinal);
        var spans = new List<(int Start, int Length, IBrush Brush)>(_tokens.Count);
        foreach (var token in _tokens)
        {
            if (token.Kind == TokenKind.Text)
            {
                continue;
            }

            // Token columns are character columns; the display text has tabs expanded.
            var start = hasTabs ? TextColumns.ToVisual(raw, token.Start, tabSize) : token.Start;
            var end = hasTabs ? TextColumns.ToVisual(raw, token.Start + token.Length, tabSize) : token.Start + token.Length;
            start = Math.Max(start, sliceStart) - sliceStart;
            end = Math.Min(end, sliceStart + sliceLength) - sliceStart;
            if (end > start)
            {
                spans.Add((start, end - start, SyntaxColors.For(token.Kind)));
            }
        }

        return spans;
    }
}
