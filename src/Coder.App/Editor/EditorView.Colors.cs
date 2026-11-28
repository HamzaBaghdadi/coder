using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Theme;
using Coder.Core.Colors;
using HsvColor = Coder.Core.Colors.HsvColor;
using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Presentation.Commands;

namespace Coder.App.Editor;

/// <summary>
/// Color decorators, like VS Code: a small swatch before every color literal (#rgb, #rrggbbaa, rgb(), hsl()…)
/// and a picker that opens when it is clicked. The swatch takes space in the line like an inlay hint, so
/// carets, selections and clicks skip over it (see <see cref="LineInsertion.Swatch"/>). Literals are found
/// once per line and cached until the line changes; only visible lines are ever scanned.
/// <para>
/// A picker session edits the document live while dragging, as one undo step: each change undoes the
/// previous one before applying the new text. Esc reverts; Enter or a click outside keeps the color.
/// </para>
/// </summary>
public sealed partial class EditorView
{
    /// <summary>The command id of "Show Color Picker" (the literal at the caret).</summary>
    public const string ShowColorPickerCommand = "editor.showColorPicker";

    /// <summary>Two cells: the swatch with a little air on both sides.</summary>
    private const string SwatchSpace = "  ";
    private const int MaxCachedColorLines = 600;
    private const double SwatchRadius = 3;

    private static readonly Cursor SwatchCursor = new(StandardCursorType.Hand);
    private static readonly LineColors NoColors = new([], []);
    private static readonly Pen SwatchBorder = new(Palette.BorderStrong, 1);

    private readonly Dictionary<int, LineColors> _colorLines = [];
    private readonly List<Token> _colorTokens = [];
    private bool _colorsEnabled;
    private ColorSyntax _colorSyntax;
    private Popup? _pickerPopup;
    private ColorPickerPopup? _picker;
    private PickerSession? _pickerSession;

    /// <summary>Whether swatches are drawn (on by default, for the languages that have color literals).</summary>
    public bool ColorDecorators { get; set; } = true;

    /// <summary>For tests: the colors with a swatch on <paramref name="line"/>.</summary>
    internal IReadOnlyList<ColorLiteral> ColorLiteralsOn(int line) => ColorsOf(line).Literals;

    /// <summary>For tests: the open picker, if any.</summary>
    internal ColorPickerPopup? OpenColorPicker => _pickerPopup?.IsOpen == true ? _picker : null;

    /// <summary>For tests: where the swatch of literal <paramref name="index"/> on <paramref name="line"/> is drawn.</summary>
    internal Rect? SwatchBounds(int line, int index) =>
        SwatchRects(line).ElementAtOrDefault(index) is var rect && rect != default ? rect : null;

    /// <summary>Adds "Show Color Picker" to the command palette (once per registry).</summary>
    internal void RegisterColorPicker(CommandRegistry commands)
    {
        if (!commands.Contains(ShowColorPickerCommand))
        {
            commands.Register(new CommandDefinition(ShowColorPickerCommand, "Editor", "Show Color Picker", null, () => ShowColorPickerAtCaret()));
        }
    }

    /// <summary>Opens the picker on the color literal at (or touching) the primary caret; false when there is none.</summary>
    public bool ShowColorPickerAtCaret()
    {
        if (_editor is null || !IsEffectivelyVisible)
        {
            return false;
        }

        var caret = _editor.CaretPosition;
        var literals = ColorsOf(caret.Line).Literals;
        foreach (var literal in literals)
        {
            if (literal.Start <= caret.Column && caret.Column <= literal.End)
            {
                OpenPicker(caret.Line, literal);
                return true;
            }
        }

        return false;
    }

    // Detection ----------------------------------------------------------------------------------

    /// <summary>The literals of a line and the swatch insertions for them (cached).</summary>
    private sealed record LineColors(IReadOnlyList<ColorLiteral> Literals, IReadOnlyList<LineInsertion> Insertions);

    private void ColorsAttached()
    {
        EndColorSession(commit: true);
        _colorLines.Clear();
        _colorsEnabled = _editor is not null && ColorLiterals.IsEnabledFor(_editor.Language.Id);
        _colorSyntax = _editor is null ? default : ColorLiterals.SyntaxFor(_editor.Language.Id);
    }

    private void InvalidateColorsFrom(int firstLine)
    {
        if (_colorLines.Count == 0)
        {
            return;
        }

        foreach (var line in _colorLines.Keys.Where(line => line >= firstLine).ToList())
        {
            _colorLines.Remove(line);
        }
    }

    /// <summary>Hints and the inline suggestion, plus a swatch before each color (after a hint at the same place).</summary>
    private IReadOnlyList<LineInsertion> InsertionsOf(int line)
    {
        var hints = HintInsertionsOf(line);
        var swatches = ColorsOf(line).Insertions;
        if (swatches.Count == 0)
        {
            return hints;
        }

        return hints.Count == 0 ? swatches : [.. hints.Concat(swatches).OrderBy(i => i.At)];
    }

    private LineColors ColorsOf(int line)
    {
        if (!_colorsEnabled || !ColorDecorators || _editor is null || line < 0 || line >= _editor.Document.LineCount)
        {
            return NoColors;
        }

        if (_colorLines.TryGetValue(line, out var cached))
        {
            return cached;
        }

        if (_colorLines.Count > MaxCachedColorLines)
        {
            _colorLines.Clear(); // only lines near the viewport are asked for; they come back on the next frame
        }

        var colors = FindColors(line);
        _colorLines[line] = colors;
        return colors;
    }

    private LineColors FindColors(int line)
    {
        var text = _editor!.Document.GetLineText(line);
        if (text.Length > LineLayoutCache.MaxCachedLineLength || text.AsSpan().IndexOfAny('#', '(') < 0)
        {
            return NoColors;
        }

        var literals = new List<ColorLiteral>();
        ColorLiterals.Find(text, _colorSyntax, literals);
        if (literals.Count > 0 && _highlighter is not null)
        {
            // Colors in comments are left alone (commented-out code, docs about colors).
            _highlighter.GetTokens(line, _colorTokens);
            literals.RemoveAll(literal => _colorTokens.Any(t => t.Kind == TokenKind.Comment && t.Start <= literal.Start && literal.Start < t.Start + t.Length));
        }

        if (literals.Count == 0)
        {
            return NoColors;
        }

        var insertions = literals
            .Select(literal => new LineInsertion(DisplayIndex(text, literal.Start), SwatchSpace, false, false, CaretAfter: true, Swatch: ToAvalonia(literal.Color)))
            .ToList();
        return new LineColors(literals, insertions);
    }

    private static Color ToAvalonia(RgbaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    // Drawing ------------------------------------------------------------------------------------

    private void DrawColorSwatches(DrawingContext context, int firstLine, int lastLine)
    {
        for (var line = firstLine; line <= lastLine; line = NextLine(line))
        {
            if (ColorsOf(line).Insertions.Count == 0)
            {
                continue;
            }

            var colors = ColorsOf(line).Literals;
            var index = 0;
            foreach (var rect in SwatchRects(line))
            {
                if (rect != default && index < colors.Count)
                {
                    DrawSwatch(context, rect, ToAvalonia(colors[index].Color));
                }

                index++;
            }
        }
    }

    /// <summary>A rounded square of the color, over a checkerboard when it is translucent, with a hairline border.</summary>
    internal static void DrawSwatch(DrawingContext context, Rect rect, Color color)
    {
        using (context.PushClip(new RoundedRect(rect, SwatchRadius)))
        {
            if (color.A < 255)
            {
                Checkerboard.Draw(context, rect, Math.Max(2, Math.Round(rect.Width / 3)));
            }

            context.FillRectangle(new SolidColorBrush(color), rect);
        }

        context.DrawRectangle(null, SwatchBorder, rect.Deflate(0.5), SwatchRadius, SwatchRadius);
    }

    /// <summary>The swatch squares of a line in view coordinates, one per literal (default when not laid out).</summary>
    private IEnumerable<Rect> SwatchRects(int line)
    {
        if (ColorsOf(line).Insertions.Count == 0)
        {
            yield break;
        }

        var layout = LayoutOf(line);
        var left = LayoutLeft(layout);
        var size = Math.Round(_metrics.FontSize * 0.8);
        var top = Math.Round(LineTop(line) + (_metrics.LineHeight - size) / 2);
        var spans = layout.InsertionSpans().Where(s => s.Insertion.Swatch is not null).ToList();
        for (var i = 0; i < ColorsOf(line).Literals.Count; i++)
        {
            if (i >= spans.Count)
            {
                yield return default; // a sliced long line has no swatches
                continue;
            }

            var (_, spanLeft, spanRight) = spans[i];
            var x = Math.Round(left + (spanLeft + spanRight - size) / 2);
            yield return new Rect(x, top, size, size);
        }
    }

    /// <summary>The literal whose swatch is under <paramref name="point"/> (view coordinates).</summary>
    private (int Line, ColorLiteral Literal)? SwatchAt(Point point)
    {
        if (_editor is null || _highlighter is null || !_colorsEnabled || point.X < GutterWidth)
        {
            return null;
        }

        var line = LineAtY(point.Y);
        if (line < 0 || line >= _editor.Document.LineCount)
        {
            return null;
        }

        var literals = ColorsOf(line).Literals;
        var index = 0;
        foreach (var rect in SwatchRects(line))
        {
            if (rect != default && rect.Inflate(2).Contains(point) && index < literals.Count)
            {
                return (line, literals[index]);
            }

            index++;
        }

        return null;
    }

    private bool TryClickColorSwatch(Point point)
    {
        if (SwatchAt(point) is not { } hit)
        {
            return false;
        }

        var (line, literal) = hit;
        _editor!.SetCaret(_editor.Document.GetLineStart(line) + literal.Start);
        OpenPicker(line, literal);
        return true;
    }

    // Picker -------------------------------------------------------------------------------------

    /// <summary>One opening of the picker: where the literal is and what the document said before.</summary>
    private sealed class PickerSession(EditorState editor, int start, string originalText)
    {
        public EditorState Editor { get; } = editor;

        public int Start { get; } = start;

        public string OriginalText { get; } = originalText;

        /// <summary>Length of the literal as the document has it now.</summary>
        public int CurrentLength { get; set; } = originalText.Length;

        /// <summary>The document version right after this session's own edit (-1: not edited).</summary>
        public int EditedVersion { get; set; } = -1;
    }

    private void OpenPicker(int line, ColorLiteral literal)
    {
        EndColorSession(commit: true);
        var document = _editor!.Document;
        var start = document.GetLineStart(line) + literal.Start;
        _pickerSession = new PickerSession(_editor, start, document.GetText(start, literal.Length));

        EnsurePicker();
        var left = CaretX(line, literal.Start) - _metrics.CharWidth * SwatchSpace.Length;
        var right = CaretX(line, literal.End);
        _pickerPopup!.PlacementRect = new Rect(left, LineTop(line), Math.Max(1, right - left), _metrics.LineHeight);
        _picker!.Show(literal.Color, literal.Style, _colorSyntax);
        _pickerPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => _picker.FocusFirst(), DispatcherPriority.Input);
        InvalidateVisual();
    }

    private void EnsurePicker()
    {
        if (_pickerPopup is not null)
        {
            return;
        }

        _picker = new ColorPickerPopup();
        _pickerPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 2,
            IsLightDismissEnabled = true,
            ShouldUseOverlayLayer = true, // stays inside the window, like the other editor overlays
            Child = _picker,
        };
        LogicalChildren.Add(_pickerPopup);

        _picker.ColorChanged += ApplyPickerColor;
        _picker.Committed += () => EndColorSession(commit: true);
        _picker.Reverted += () => EndColorSession(commit: false);
        _pickerPopup.Closed += (_, _) => EndColorSession(commit: true); // a click outside keeps the color
        Scrolled += (_, _) => EndColorSession(commit: true);            // the popup would no longer sit under its literal
    }

    /// <summary>Writes the picked color into the document, replacing this session's previous edit (one undo step).</summary>
    private void ApplyPickerColor(RgbaColor color, ColorStyle style)
    {
        if (_pickerSession is not { } session)
        {
            return;
        }

        var document = session.Editor.Document;
        if (session.Start + session.CurrentLength > document.Length)
        {
            return;
        }

        var text = ColorLiterals.Format(color, style);
        if (text == document.GetText(session.Start, session.CurrentLength))
        {
            return;
        }

        if (session.EditedVersion == document.Version)
        {
            session.Editor.Undo(); // back to the original, so the whole session stays one undo step
            session.CurrentLength = session.OriginalText.Length;
            session.EditedVersion = -1;
            if (text == session.OriginalText)
            {
                return;
            }
        }

        session.Editor.ApplyEdits([(session.Start, session.Start + session.CurrentLength, text)]);
        session.CurrentLength = text.Length;
        session.EditedVersion = document.Version;
    }

    /// <summary>Ends the session: keeps the edit (commit) or puts the original text back, then closes the popup.</summary>
    private void EndColorSession(bool commit)
    {
        if (_pickerSession is not { } session)
        {
            return;
        }

        _pickerSession = null;
        var document = session.Editor.Document;
        if (!commit && session.Start + session.CurrentLength <= document.Length)
        {
            if (session.EditedVersion == document.Version)
            {
                session.Editor.Undo();
            }
            else if (document.GetText(session.Start, session.CurrentLength) != session.OriginalText)
            {
                session.Editor.ApplyEdits([(session.Start, session.Start + session.CurrentLength, session.OriginalText)]);
            }
        }

        if (_pickerPopup is { IsOpen: true })
        {
            var hadFocus = _picker!.IsKeyboardFocusWithin;
            _pickerPopup.IsOpen = false;
            if (hadFocus && ReferenceEquals(session.Editor, _editor))
            {
                Focus();
            }
        }

        InvalidateVisual();
    }
}
