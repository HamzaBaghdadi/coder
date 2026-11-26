using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Theme;
using Coder.Core.Lsp;
using Coder.Presentation.Editors;

namespace Coder.App.Editor;

/// <summary>
/// What the view does for language features: draws diagnostics as squiggles, notices when the
/// pointer rests on a word (for hover cards) and turns Ctrl+Click into "go to definition". The
/// features themselves live outside the view; it only reports positions and draws.
/// </summary>
public sealed partial class EditorView
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(450);

    private Func<IReadOnlyList<DiagnosticMarker>> _diagnostics = () => [];
    private DispatcherTimer? _hoverTimer;
    private (int Start, int End)? _hoverWord;
    private int _hoverOffset;

    /// <summary>The pointer rested on a word: the argument is the document offset under it.</summary>
    public event EventHandler<int>? HoverRequested;

    /// <summary>The pointer left the word it rested on (or the view).</summary>
    public event EventHandler? HoverEnded;

    /// <summary>Ctrl+Click on a symbol; the caret is already there.</summary>
    public event EventHandler? DefinitionRequested;

    /// <summary>Where the diagnostics of the shown document come from (read on every repaint, so they follow edits).</summary>
    public void SetDiagnosticsSource(Func<IReadOnlyList<DiagnosticMarker>> diagnostics)
    {
        _diagnostics = diagnostics;
        InvalidateVisual();
    }

    /// <summary>The box of the character at <paramref name="offset"/> in view coordinates.</summary>
    public Rect CharacterBounds(int offset)
    {
        var position = _editor!.Document.GetPosition(Math.Clamp(offset, 0, _editor.Document.Length));
        var x = CaretX(position.Line, position.Column);
        return new Rect(x, LineTop(position.Line), _metrics.CharWidth, _metrics.LineHeight);
    }

    // Diagnostics -------------------------------------------------------------------------------

    /// <summary>A wavy underline below each problem: red errors, amber warnings, blue info; hints are dotted.</summary>
    private void DrawDiagnostics(DrawingContext context, int firstLine, int lastLine)
    {
        var markers = _diagnostics();
        if (markers.Count == 0)
        {
            return;
        }

        var document = _editor!.Document;
        var visibleStart = document.GetLineStart(firstLine);
        var visibleEnd = document.GetLineEnd(lastLine);
        foreach (var marker in markers.Where(m => m.End >= visibleStart && m.Start <= visibleEnd).OrderByDescending(m => m.Severity))
        {
            var start = document.GetPosition(Math.Min(marker.Start, document.Length));
            var end = document.GetPosition(Math.Min(marker.End, document.Length));
            var pen = DiagnosticPen(marker.Severity);
            for (var line = Math.Max(firstLine, start.Line); line <= Math.Min(lastLine, end.Line); line++)
            {
                var from = line == start.Line ? start.Column : 0;
                var to = line == end.Line ? end.Column : document.GetLineLength(line);
                var baseline = LineTop(line) + _metrics.LineHeight - 2;
                if (from == to)
                {
                    // An empty range (a missing token) still needs to be visible: one character wide.
                    var x = CaretX(line, from);
                    DrawSquiggle(context, pen, x, x + _metrics.CharWidth, baseline, marker.Severity);
                    continue;
                }

                foreach (var (left, right) in SpansOf(line, from, to))
                {
                    DrawSquiggle(context, pen, left, right, baseline, marker.Severity);
                }
            }
        }
    }

    private static void DrawSquiggle(DrawingContext context, Pen pen, double left, double right, double y, DiagnosticSeverity severity)
    {
        if (severity == DiagnosticSeverity.Hint)
        {
            context.DrawLine(pen, new Point(left, y + 1), new Point(Math.Min(right, left + 12), y + 1));
            return;
        }

        const double Half = 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(left, y + 1), isFilled: false);
            var up = true;
            for (var x = left + Half; x <= right + Half; x += Half)
            {
                g.LineTo(new Point(x, up ? y - 0.5 : y + 1.5));
                up = !up;
            }

            g.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static readonly Pen ErrorPen = new(Palette.DiagnosticError, 1);
    private static readonly Pen WarningPen = new(Palette.DiagnosticWarning, 1);
    private static readonly Pen InfoPen = new(Palette.DiagnosticInfo, 1);
    private static readonly Pen HintPen = new(Palette.DiagnosticHint, 1, new DashStyle([1, 2], 0));

    private static Pen DiagnosticPen(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => ErrorPen,
        DiagnosticSeverity.Warning => WarningPen,
        DiagnosticSeverity.Information => InfoPen,
        _ => HintPen,
    };

    // Hover -------------------------------------------------------------------------------------

    /// <summary>Restarts the rest timer when the pointer moves onto a different word.</summary>
    private void TrackHover(Point position)
    {
        if (_editor is null || _dragUnit is not null || position.X < GutterWidth)
        {
            EndHover();
            return;
        }

        var offset = OffsetAt(position);
        var line = _editor.Document.GetLineOfOffset(offset);
        var pastLineEnd = position.X > LineEndX(line) + _metrics.CharWidth || position.Y + _offset.Y > _editor.Document.LineCount * _metrics.LineHeight;
        var word = pastLineEnd ? ((int Start, int End)?)null : _editor.WordRangeAt(offset);
        if (word == _hoverWord)
        {
            return;
        }

        EndHover();
        if (word is not { } range || range.Start == range.End)
        {
            return;
        }

        _hoverWord = range;
        _hoverOffset = offset;
        _hoverTimer ??= new DispatcherTimer(HoverDelay, DispatcherPriority.Input, (_, _) =>
        {
            _hoverTimer!.Stop();
            HoverRequested?.Invoke(this, _hoverOffset);
        });
        _hoverTimer.Start();
    }

    private void EndHover()
    {
        _hoverTimer?.Stop();
        if (_hoverWord is not null)
        {
            _hoverWord = null;
            HoverEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        EndHover();
        MinimapPointerExited();
        _pointer = null;
        UpdateCtrlLink(null, KeyModifiers.None);
    }

    /// <summary>Ctrl+Click: put the caret on the symbol and ask for its definition.</summary>
    private bool TryGoToDefinition(PointerPressedEventArgs e, int offset)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.ClickCount != 1)
        {
            return false;
        }

        _editor!.SetCaret(offset);
        DefinitionRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
